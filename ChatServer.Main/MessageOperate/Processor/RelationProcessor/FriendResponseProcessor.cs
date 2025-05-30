﻿using AutoMapper;
using ChatServer.Common;
using ChatServer.Common.Protobuf;
using ChatServer.DataBase.DataBase.DataEntity;
using ChatServer.DataBase.DataBase.UnitOfWork;
using ChatServer.Main.Entity;
using ChatServer.Main.IOServer.Manager;
using ChatServer.Main.Services;
using ChatServer.Main.Services.Helper;
using DotNetty.Transport.Channels;

namespace ChatServer.Main.MessageOperate.Processor.RelationProcessor;

/// <summary>
/// 处理目标：
/// FriendResponseFromClient 来自某个客户对好友请求的处理结果
/// 
/// 数据库操作：
/// 1、FriendRelation 将好友关系保存到数据库
/// 2、FriendRequest 更新好友请求状态
/// 
/// 需要发送的消息: 
/// 1、FriendResponseFromUserResponse (To:发送方) 用于通知发送方好友请求是否发送成功
/// 2、FriendResponseFromServer (To:请求方) 用于通知请求方有人向他发送了好友请求
/// 3、NewFriendMessage (To:发送方和请求方) 用于通知双方成为好友
/// </summary>
public class FriendResponseProcessor : IProcessor<FriendResponseFromClient>
{
    private readonly IUnitOfWork unitOfWork;
    private readonly IMapper mapper;
    private readonly IFriendService friendService;
    private readonly IClientChannelManager clientChannel;

    public FriendResponseProcessor(IUnitOfWork unitOfWork,
        IMapper mapper,
        IFriendService friendService,
        IClientChannelManager clientChannel)
    {
        this.unitOfWork = unitOfWork;
        this.mapper = mapper;
        this.friendService = friendService;
        this.clientChannel = clientChannel;
    }

    public async Task Process(MessageUnit<FriendResponseFromClient> unit)
    {
        unit.Channel.TryGetTarget(out IChannel? channel);

        #region Step 1
        //-- 操作：获取数据库中的RequestId对应的好友请求 --//
        var repository = unitOfWork.GetRepository<FriendRequest>();
        var request = await repository.GetFirstOrDefaultAsync(predicate: x => x.Id.Equals(unit.Message.RequestId), disableTracking: false);
        #endregion


        #region Step 2
        //-- 判断：数据库中是否存在RequestId对应的好友请求 --//
        if (request == null)
        {
            if (channel != null)
            {
                CommonResponse commonResponse = new CommonResponse
                {
                    State = false,
                    Message = "不存在此好友请求"
                };
                await channel.WriteAndFlushProtobufAsync(commonResponse);
            }
            return;
        }
        #endregion


        #region Step 3
        //-- 判断：是否已经处理过此好友请求 --//
        if (request.IsSolved)
        {
            if (channel != null)
            {
                CommonResponse commonResponse = new CommonResponse
                {
                    State = false,
                    Message = "此好友请求已经处理过了"
                };
                await channel.WriteAndFlushProtobufAsync(commonResponse);
            }
            return;
        }
        #endregion


        #region Step 4
        // 检查是否已经是好友
        bool isFriend = await friendService.IsFriend(request.UserFromId, request.UserTargetId);

        request.IsSolved = true;
        request.IsAccept = unit.Message.Accept;
        request.SolveTime = DateTime.Parse(unit.Message.ResponseTime);
        IChannel? source = clientChannel.GetClient(request.UserFromId);
        #endregion


        #region Step 5
        //-- 判断：如果接收方不是好友，且接收方同意添加好友，则保存好友关系并添加聊天消息 --//
        if (unit.Message.Accept && !isFriend)
        {
            // --  Step1 ： 数据库保存 -- //
            bool isSucceed = true;
            FriendChatMessage? friendChatMessage = null;
            try
            {
                var friendRepository = unitOfWork.GetRepository<FriendRelation>();
                FriendRelation relationSource = new FriendRelation
                {
                    User1Id = request.UserFromId,
                    User2Id = request.UserTargetId,
                    Grouping = request.Group,
                    Remark = request.Remark,
                    GroupTime = DateTime.Parse(unit.Message.ResponseTime)
                };
                FriendRelation relationTarget = new FriendRelation
                {
                    User1Id = request.UserTargetId,
                    User2Id = request.UserFromId,
                    Remark = unit.Message.Remark,
                    Grouping = unit.Message.Group,
                    GroupTime = DateTime.Parse(unit.Message.ResponseTime)
                };
                await friendRepository.InsertAsync(relationSource);
                await friendRepository.InsertAsync(relationTarget);

                var chatPrivateRepository = unitOfWork.GetRepository<ChatPrivate>();
                ChatPrivate chatPrivate = new ChatPrivate
                {
                    UserFromId = request.UserTargetId,
                    UserTargetId = request.UserFromId,
                    Message = ChatMessageHelper.EncruptChatMessage([new ChatMessage
                    {
                        TextMess = new TextMess { Text = "我们已经成为好友了，开始聊天吧！"}
                    }]),
                    IsRetracted = false,
                    Time = DateTime.Now,
                    RetractTime = DateTime.MinValue
                };
                await chatPrivateRepository.InsertAsync(chatPrivate);

                await unitOfWork.SaveChangesAsync();

                friendChatMessage = mapper.Map<FriendChatMessage>(chatPrivate);
            }
            catch { isSucceed = false; }

            // --  Step2 ： 如果成功保存数据，通知双方成为好友 -- //
            if (isSucceed)
            {
                // 发送方消息
                if (source != null)
                {
                    NewFriendMessage newFriendSource = new NewFriendMessage
                    {
                        RelationTime = request.SolveTime.ToString(),
                        Grouping = request.Group,
                        Remark = request.Remark,
                        FrinedId = request.UserTargetId,
                        UserId = request.UserFromId,
                    };
                    await source.WriteAndFlushProtobufAsync(newFriendSource);

                    if (friendChatMessage != null)
                        _ = Task.Delay(150).ContinueWith(async t =>
                        {
                           await source.WriteAndFlushProtobufAsync(friendChatMessage);
                        });
                }

                // 接收方消息
                if (channel != null)
                {
                    NewFriendMessage newFriendTarget = new NewFriendMessage
                    {
                        RelationTime = request.SolveTime.ToString(),
                        Grouping = unit.Message.Group,
                        Remark = unit.Message.Remark,
                        FrinedId = request.UserFromId,
                        UserId = request.UserTargetId
                    };
                    await channel.WriteAndFlushProtobufAsync(newFriendTarget);

                    if (friendChatMessage != null)
                        _ = Task.Delay(150).ContinueWith(async t =>
                        {
                            await channel.WriteAndFlushProtobufAsync(friendChatMessage);
                        });
                }
            }
        }
        #endregion


        #region Step 6
        //-- 处理：为接收方和发送方发送FriendRequest响应，用于处理客户端本地数据库中好友请求状态 --//

        // 返回接收方响应，表示处理成功
        if (channel != null)
        {
            string message = isFriend ? "你们已经是好友了" : "好友请求处理成功";
            FriendResponseFromClientResponse response = new FriendResponseFromClientResponse
            {
                Response = new CommonResponse { State = true, Message = message }
            };
            await channel.WriteAndFlushProtobufAsync(response);
        }

        // 返回发送方响应，表示好友消息处理成功
        if (source != null)
        {
            FriendResponseFromServer responseFromServer = new FriendResponseFromServer
            {
                Accept = unit.Message.Accept,
                RequestId = request.Id,
                ResponseTime = request.SolveTime.ToString()
            };
            await source.WriteAndFlushProtobufAsync(responseFromServer);
        }
        #endregion
    }
}
