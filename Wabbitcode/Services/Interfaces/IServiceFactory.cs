﻿using System;

namespace Revsoft.Wabbitcode.Services.Interfaces
{
    public interface IServiceFactory
    {
        T CreateServiceInstance<T>()
        where T : IService;

        T CreateServiceInstance<T>(params Object[] objects)
        where T : IService;

        void DestroyServiceInstance<T>()
        where T : IService;

        T GetServiceInstance<T>()
        where T : IService;

        T GetServiceInstance<T>(params Object[] objects)
        where T : IService;
    }
}