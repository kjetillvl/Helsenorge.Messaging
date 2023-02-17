﻿/* 
 * Copyright (c) 2020-2022, Norsk Helsenett SF and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the MIT license
 * available at https://raw.githubusercontent.com/helsenorge/Helsenorge.Messaging/master/LICENSE
 */

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Helsenorge.Messaging.Abstractions;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Helsenorge.Messaging.Amqp
{
    internal class AmqpFactoryPool : MessagingEntityCache<IMessagingFactory>, IAmqpFactoryPool
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly BusSettings _settings;
        private readonly IDictionary<string, object> _applicationProperties;
        private int _index;
        private IMessagingFactory _alternateMessagingFactor;

        public AmqpFactoryPool(BusSettings settings, IDictionary<string, object> applicationProperties = null) :
            base("FactoryPool", settings.MaxFactories, settings.CacheEntryTimeToLive, settings.MaxCacheEntryTrimCount)
        {
            _settings = settings;
            _applicationProperties = applicationProperties;
        }

        public void RegisterAlternateMessagingFactoryAsync(IMessagingFactory alternateMessagingFactory)
        {
            _alternateMessagingFactor = alternateMessagingFactory;
        }

        [ExcludeFromCodeCoverage] // Azure service bus implementation
        protected override Task<IMessagingFactory> CreateEntityAsync(ILogger logger, string id)
        {
            if (_alternateMessagingFactor != null) return Task.FromResult(_alternateMessagingFactor);
            var connection = new AmqpConnection(_settings.ConnectionString?.ToString(), _settings.MessageBrokerDialect, _settings.MaxLinksPerSession, _settings.MaxSessionsPerConnection);
            return Task.FromResult<IMessagingFactory>(new AmqpFactory(logger, connection, _applicationProperties));
        }
        public async Task<IMessagingMessage> CreateMessageAsync(ILogger logger, Stream stream)
        {
            var factory = await FindNextFactoryAsync(logger).ConfigureAwait(false);
            return await factory.CreateMessageAsync(stream).ConfigureAwait(false);
        }
        public async Task<IMessagingFactory> FindNextFactoryAsync(ILogger logger)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // increas value in a round-robin fashion
                _index++;
                if (_index == Capacity)
                {
                    _index = 0;
                    // Make sure we do not increment ActiveCount any further after we have created all the needed
                    // BusFactory instances.
                    if(_incrementActiveCount) _incrementActiveCount = false;
                }
                var name = string.Format(null, "MessagingFactory{0}", _index);
                var factory = await CreateAsync(logger, name).ConfigureAwait(false);

                return factory;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}