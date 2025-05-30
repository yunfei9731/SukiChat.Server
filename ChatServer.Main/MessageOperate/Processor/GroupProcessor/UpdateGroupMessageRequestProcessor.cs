﻿using ChatServer.Common;
using ChatServer.Common.Protobuf;
using ChatServer.DataBase.DataBase.DataEntity;
using ChatServer.DataBase.DataBase.UnitOfWork;
using ChatServer.Main.Entity;
using ChatServer.Main.IOServer.Manager;
using ChatServer.Main.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatServer.Main.MessageOperate.Processor.GroupProcessor
{
    public class UpdateGroupMessageRequestProcessor : IProcessor<UpdateGroupMessageRequest>
    {
        private readonly IGroupService groupService;
        private readonly IUnitOfWork unitOfWork;
        private readonly IClientChannelManager channelManager;

        public UpdateGroupMessageRequestProcessor(IGroupService groupService,
            IUnitOfWork unitOfWork,
            IClientChannelManager channelManager)
        {
            this.groupService = groupService;
            this.unitOfWork = unitOfWork;
            this.channelManager = channelManager;
        }

        public async Task Process(MessageUnit<UpdateGroupMessageRequest> unit)
        {
            unit.Channel.TryGetTarget(out var channel);
            var message = unit.Message;

            // 检查User是否为Group的管理员或者群主
            if(! await groupService.IsGroupManager(message.UserId,message.GroupId))
            {
                if(channel != null)
                {
                    await channel.WriteAndFlushProtobufAsync(new UpdateGroupMessage
                    {
                        Response = new CommonResponse { State = false,Message = "你无权限更改群信息" }
                    });
                }
                return;
            }

            bool success = true;
            try
            {
                var groupRepository = unitOfWork.GetRepository<Group>();
                var group = await groupRepository.GetFirstOrDefaultAsync(predicate: d => d.Id.Equals(message.GroupId), disableTracking: false);
                group.Description = message.Description;
                group.Name = message.Name;
                await unitOfWork.SaveChangesAsync();
                groupRepository.ChangeEntityState(group, Microsoft.EntityFrameworkCore.EntityState.Detached);
            }
            catch
            {
                success = false;
            }

            if(channel != null)
            {
                await channel.WriteAndFlushProtobufAsync(new UpdateGroupMessage
                {
                    Response = new CommonResponse { State = success },
                    GroupId = message.GroupId,
                });
            }

            if (success)
            {
                var response = new UpdateGroupMessage
                {
                    GroupId = message.GroupId,
                };

                // 通知所有成员，group信息发生变化
                var memberIds = await groupService.GetGroupMembers(message.GroupId);
                foreach (var memberId in memberIds)
                {
                    if (memberId.Equals(message.UserId)) continue;
                    var member = channelManager.GetClient(memberId);
                    if(member != null)
                    {
                        await member.WriteAndFlushProtobufAsync(response);
                    }
                }
            }
        }
    }
}
