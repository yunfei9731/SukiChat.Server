﻿using ChatServer.Common;
using ChatServer.Common.Protobuf;
using ChatServer.Common.Tool;
using ChatServer.DataBase.DataBase.DataEntity;
using ChatServer.DataBase.DataBase.UnitOfWork;
using ChatServer.DataBase.UnitOfWork;
using ChatServer.Main.Entity;
using ChatServer.Main.Services;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatServer.Main.IOServer.Manager
{
    public interface IClientChannelManager
    {
        void AddClient(IChannel channel);
        void RemoveClient(IChannel? channel);
        void ClientLogin(IChannel channel, string Id);
        void ClientLogout(IChannel channel);
        bool ClientOnline(string userId);
        IChannel? GetClient(string userId);
    }

    /// <summary>
    /// 管理用户连接以及用户登录状态
    /// </summary>
    public class ClientChannelManager : IClientChannelManager
    {
        private readonly IServiceProvider serviceProvider;

        private readonly object _channelsLock = new object();
        private List<ClientChannel> channels = [];

        public ClientChannelManager(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 连接建立时调用
        /// </summary>
        /// <param name="channel"></param>
        public void AddClient(IChannel channel)
        {
            lock (_channelsLock)
            {
                int index = channels.FindIndex(c => Equals(c.Channel, channel));
                if (index == -1)
                    channels.Add(new ClientChannel(channel));
            }
        }

        /// <summary>
        /// 连接断开时调用
        /// </summary>
        /// <param name="channel"></param>
        public void RemoveClient(IChannel? channel)
        {
            if (channel == null) return;

            ClientLogout(channel);
            lock (_channelsLock)
            {
                int index = channels.FindIndex(c => Equals(c.Channel, channel));
                if (index != -1)
                    channels.RemoveAt(index);
            }
        }

        /// <summary>
        /// 用户登录时调用
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="Id"></param>
        public async void ClientLogin(IChannel channel, string Id)
        {
            ClientChannel? client_loged;
            ClientChannel? client;

            // 使用锁获取所需的引用
            lock (_channelsLock)
            {
                client_loged = channels.Find(c => c.userId != null && c.userId.Equals(Id));
                client = channels.Find(c => Equals(c.Channel, channel));
            }

            if (client_loged != null && client_loged.Channel != channel)
            {
                ClientLogout(client_loged.Channel);

                // 如果已经登录，就踢掉之前的登录
                LogoutCommand logoutCommand = new LogoutCommand
                {
                    Id = Id,
                    Message = "您的账号在其他地方登录"
                };

                await client_loged.Channel.WriteAndFlushProtobufAsync(logoutCommand);
            }

            if (client != null)
                client.Login(Id);
            else
            {
                var clientChannel = new ClientChannel(channel);
                clientChannel.Login(Id);
                lock (_channelsLock)
                    channels.Add(clientChannel);
            }

            // 通知其他在线用户，此用户已经上线
            // 获取好友服务
            var friendService = serviceProvider.GetRequiredService<IFriendService>();

            var friendIds = await friendService.GetFriendsId(Id);

            // 创建上线消息
            var loginMessage = new FriendLoginMessage
            {
                FriendId = Id,
                LoginTime = DateTime.Now.ToString()
            };

            // 向所有在线的好友发送上线消息
            foreach (var friendId in friendIds)
            {
                var friend = GetClient(friendId);
                if (friend != null)
                    await friend.WriteAndFlushProtobufAsync(loginMessage);
            }

        }

        /// <summary>
        /// 用户登出或者在连接断开时调用
        /// </summary>
        /// <param name="channel"></param>
        public async void ClientLogout(IChannel channel)
        {
            ClientChannel? client;
            List<ClientChannel> onlineUsers;

            // 使用锁获取所需的引用
            lock (_channelsLock)
            {
                client = channels.FirstOrDefault(c => Equals(channel, c.Channel));

                if (client == null)
                {
                    var clientChannel = new ClientChannel(channel);
                    channels.Add(clientChannel);
                    return; // 没有登录，直接返回
                }

                if (!client.isLogined) return;

                // 获取所有在线用户的副本
                onlineUsers = channels.Where(c => c.isLogined && c.userId != null).ToList();
            }

            // 剩余处理逻辑移到锁外部执行，避免长时间持有锁
            var loginService = serviceProvider.GetRequiredService<ILoginService>();
            await loginService.UserOutline(client);

            // 通知其他在线用户，此用户已经下线
            // 获取好友服务
            var friendService = serviceProvider.GetRequiredService<IFriendService>();

            // 创建下线消息
            var logoutMessage = new FriendLogoutMessage
            {
                FriendId = client.userId,
                LogoutTime = DateTime.Now.ToString()
            };

            // 向所有在线的好友发送下线消息
            foreach (var onlineUser in onlineUsers)
            {
                if (onlineUser.userId == null || onlineUser.userId.Equals(client.userId)) continue;
                // 判断是否为好友
                if (await friendService.IsFriend(onlineUser.userId, client.userId!))
                {
                    // 发送下线消息
                    await onlineUser.Channel.WriteAndFlushProtobufAsync(logoutMessage);
                }
            }

            client.Logout();
        }

        public bool ClientOnline(string userId)
        {
            lock (_channelsLock)
                return channels.Exists(c => c.userId != null && c.userId.Equals(userId));
        }

        public IChannel? GetClient(string userId)
        {
            lock (_channelsLock)
                return channels.FirstOrDefault(c => c.userId != null && c.userId.Equals(userId))?.Channel;
        }
    }
}
