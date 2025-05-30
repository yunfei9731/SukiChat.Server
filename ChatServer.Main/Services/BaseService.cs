﻿using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatServer.Main.Services
{
    public abstract class BaseService:IDisposable
    {
        protected readonly IServiceScope _scopedProvider;

        public BaseService(IServiceProvider serviceProvider)
        {
            _scopedProvider = serviceProvider.CreateScope();
        }

        public void Dispose()
        {
            _scopedProvider.Dispose();
        }
    }
}
