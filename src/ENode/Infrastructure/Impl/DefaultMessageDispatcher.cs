﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ECommon.Extensions;
using ECommon.IO;
using ECommon.Logging;

namespace ENode.Infrastructure.Impl
{
    public class DefaultMessageDispatcher : IMessageDispatcher
    {
        #region Private Variables

        private readonly ITypeCodeProvider _typeCodeProvider;
        private readonly IMessageHandlerProvider _handlerProvider;
        private readonly ITwoMessageHandlerProvider _twoMessageHandlerProvider;
        private readonly IThreeMessageHandlerProvider _threeMessageHandlerProvider;
        private readonly IMessageHandleRecordStore _messageHandleRecordStore;
        private readonly IOHelper _ioHelper;
        private readonly ILogger _logger;

        #endregion

        #region Constructors

        public DefaultMessageDispatcher(
            ITypeCodeProvider typeCodeProvider,
            IMessageHandlerProvider handlerProvider,
            ITwoMessageHandlerProvider twoMessageHandlerProvider,
            IThreeMessageHandlerProvider threeMessageHandlerProvider,
            IMessageHandleRecordStore messageHandleRecordStore,
            IOHelper ioHelper,
            ILoggerFactory loggerFactory)
        {
            _typeCodeProvider = typeCodeProvider;
            _handlerProvider = handlerProvider;
            _twoMessageHandlerProvider = twoMessageHandlerProvider;
            _threeMessageHandlerProvider = threeMessageHandlerProvider;
            _messageHandleRecordStore = messageHandleRecordStore;
            _ioHelper = ioHelper;
            _logger = loggerFactory.Create(GetType().FullName);
        }

        #endregion

        public Task<AsyncTaskResult> DispatchMessageAsync(IMessage message)
        {
            return DispatchMessages(new List<IMessage> { message });
        }
        public Task<AsyncTaskResult> DispatchMessagesAsync(IEnumerable<IMessage> messages)
        {
            return DispatchMessages(messages);
        }

        #region Private Methods

        private Task<AsyncTaskResult> DispatchMessages(IEnumerable<IMessage> messages)
        {
            var messageCount = messages.Count();
            if (messageCount == 0)
            {
                return Task.FromResult<AsyncTaskResult>(AsyncTaskResult.Success);
            }
            var rootDispatching = new RootDisptaching();

            //先对每个事件调用其Handler
            var queueMessageDispatching = new QueueMessageDisptaching(this, rootDispatching, messages);
            DispatchSingleMessage(queueMessageDispatching.DequeueMessage(), queueMessageDispatching);

            //如果有至少两个事件，则尝试调用针对两个事件的Handler
            if (messageCount >= 2)
            {
                var twoMessageHandlers = _twoMessageHandlerProvider.GetHandlers(messages.Select(x => x.GetType()));
                if (twoMessageHandlers.IsNotEmpty())
                {
                    DispatchMultiMessage(messages, twoMessageHandlers, rootDispatching, DispatchTwoMessageToHandlerAsync);
                }
            }
            //如果有至少三个事件，则尝试调用针对三个事件的Handler
            if (messageCount >= 3)
            {
                var threeMessageHandlers = _threeMessageHandlerProvider.GetHandlers(messages.Select(x => x.GetType()));
                if (threeMessageHandlers.IsNotEmpty())
                {
                    DispatchMultiMessage(messages, threeMessageHandlers, rootDispatching, DispatchThreeMessageToHandlerAsync);
                }
            }
            return rootDispatching.Task;
        }

        private void DispatchSingleMessage(IMessage message, QueueMessageDisptaching queueMessageDispatching)
        {
            var messageHandlerDataList = _handlerProvider.GetHandlers(message.GetType());
            if (!messageHandlerDataList.Any())
            {
                queueMessageDispatching.OnMessageHandled(message);
                return;
            }

            foreach (var messageHandlerData in messageHandlerDataList)
            {
                var singleMessageDispatching = new SingleMessageDisptaching(message, queueMessageDispatching, messageHandlerData.AllHandlers, _typeCodeProvider);

                if (messageHandlerData.ListHandlers != null && messageHandlerData.ListHandlers.IsNotEmpty())
                {
                    foreach (var handler in messageHandlerData.ListHandlers)
                    {
                        DispatchSingleMessageToHandlerAsync(singleMessageDispatching, handler, null, 0);
                    }
                }
                if (messageHandlerData.QueuedHandlers != null && messageHandlerData.QueuedHandlers.IsNotEmpty())
                {
                    var queueHandler = new QueuedHandler<IMessageHandlerProxy1>(messageHandlerData.QueuedHandlers, (queuedHandler, nextHandler) => DispatchSingleMessageToHandlerAsync(singleMessageDispatching, nextHandler, queuedHandler, 0));
                    DispatchSingleMessageToHandlerAsync(singleMessageDispatching, queueHandler.DequeueHandler(), queueHandler, 0);
                }
            }
        }
        private void DispatchMultiMessage<T>(IEnumerable<IMessage> messages, IEnumerable<MessageHandlerData<T>> messageHandlerDataList, RootDisptaching rootDispatching, Action<MultiMessageDisptaching, T, QueuedHandler<T>, int> dispatchAction) where T : class, IHandlerProxy
        {
            foreach (var messageHandlerData in messageHandlerDataList)
            {
                var multiMessageDispatching = new MultiMessageDisptaching(messages, messageHandlerData.AllHandlers, rootDispatching, _typeCodeProvider);

                if (messageHandlerData.ListHandlers != null && messageHandlerData.ListHandlers.IsNotEmpty())
                {
                    foreach (var handler in messageHandlerData.ListHandlers)
                    {
                        dispatchAction(multiMessageDispatching, handler, null, 0);
                    }
                }
                if (messageHandlerData.QueuedHandlers != null && messageHandlerData.QueuedHandlers.IsNotEmpty())
                {
                    var queuedHandler = new QueuedHandler<T>(messageHandlerData.QueuedHandlers,
                        (currentQueuedHandler, nextHandler) => dispatchAction(multiMessageDispatching, nextHandler, currentQueuedHandler, 0));
                    dispatchAction(multiMessageDispatching, queuedHandler.DequeueHandler(), queuedHandler, 0);
                }
            }
        }

        private void DispatchSingleMessageToHandlerAsync(SingleMessageDisptaching singleMessageDispatching, IMessageHandlerProxy1 handlerProxy, QueuedHandler<IMessageHandlerProxy1> queueHandler, int retryTimes)
        {
            var message = singleMessageDispatching.Message;
            var messageTypeCode = _typeCodeProvider.GetTypeCode(message.GetType());
            var handlerType = handlerProxy.GetInnerHandler().GetType();
            var handlerTypeCode = _typeCodeProvider.GetTypeCode(handlerType);
            var aggregateRootTypeCode = message is ISequenceMessage ? ((ISequenceMessage)message).AggregateRootTypeCode : 0;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<bool>>("IsRecordExistAsync",
            () => _messageHandleRecordStore.IsRecordExistAsync(message.Id, handlerTypeCode, aggregateRootTypeCode),
            currentRetryTimes => DispatchSingleMessageToHandlerAsync(singleMessageDispatching, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                if (result.Data)
                {
                    singleMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                    if (queueHandler != null)
                    {
                        queueHandler.OnHandlerFinished(handlerProxy);
                    }
                }
                else
                {
                    HandleSingleMessageAsync(singleMessageDispatching, handlerProxy, handlerTypeCode, messageTypeCode, queueHandler, 0);
                }
            },
            () => string.Format("[messageId:{0}, messageType:{1}, handlerType:{2}]", message.Id, message.GetType().Name, handlerType.Name),
            null,
            retryTimes,
            true);
        }
        private void DispatchTwoMessageToHandlerAsync(MultiMessageDisptaching multiMessageDispatching, IMessageHandlerProxy2 handlerProxy, QueuedHandler<IMessageHandlerProxy2> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;
            var message1 = messages[0];
            var message2 = messages[1];
            var handlerType = handlerProxy.GetInnerHandler().GetType();
            var handlerTypeCode = _typeCodeProvider.GetTypeCode(handlerType);
            var aggregateRootTypeCode = message1 is ISequenceMessage ? ((ISequenceMessage)message1).AggregateRootTypeCode : 0;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<bool>>("IsTwoMessageRecordExistAsync",
            () => _messageHandleRecordStore.IsRecordExistAsync(message1.Id, message2.Id, handlerTypeCode, aggregateRootTypeCode),
            currentRetryTimes => DispatchTwoMessageToHandlerAsync(multiMessageDispatching, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                if (result.Data)
                {
                    multiMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                    if (queueHandler != null)
                    {
                        queueHandler.OnHandlerFinished(handlerProxy);
                    }
                }
                else
                {
                    HandleTwoMessageAsync(multiMessageDispatching, handlerProxy, handlerTypeCode, queueHandler, 0);
                }
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }
        private void DispatchThreeMessageToHandlerAsync(MultiMessageDisptaching multiMessageDispatching, IMessageHandlerProxy3 handlerProxy, QueuedHandler<IMessageHandlerProxy3> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;
            var message1 = messages[0];
            var message2 = messages[1];
            var message3 = messages[2];
            var handlerType = handlerProxy.GetInnerHandler().GetType();
            var handlerTypeCode = _typeCodeProvider.GetTypeCode(handlerType);
            var aggregateRootTypeCode = message1 is ISequenceMessage ? ((ISequenceMessage)message1).AggregateRootTypeCode : 0;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult<bool>>("IsThreeMessageRecordExistAsync",
            () => _messageHandleRecordStore.IsRecordExistAsync(message1.Id, message2.Id, message3.Id, handlerTypeCode, aggregateRootTypeCode),
            currentRetryTimes => DispatchThreeMessageToHandlerAsync(multiMessageDispatching, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                if (result.Data)
                {
                    multiMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                    if (queueHandler != null)
                    {
                        queueHandler.OnHandlerFinished(handlerProxy);
                    }
                }
                else
                {
                    HandleThreeMessageAsync(multiMessageDispatching, handlerProxy, handlerTypeCode, queueHandler, 0);
                }
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }

        private void HandleSingleMessageAsync(SingleMessageDisptaching singleMessageDispatching, IMessageHandlerProxy1 handlerProxy, int handlerTypeCode, int messageTypeCode, QueuedHandler<IMessageHandlerProxy1> queueHandler, int retryTimes)
        {
            var message = singleMessageDispatching.Message;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("HandleSingleMessageAsync",
            () => handlerProxy.HandleAsync(message),
            currentRetryTimes => HandleSingleMessageAsync(singleMessageDispatching, handlerProxy, handlerTypeCode, messageTypeCode, queueHandler, currentRetryTimes),
            result =>
            {
                var messageHandleRecord = new MessageHandleRecord
                {
                    MessageId = message.Id,
                    MessageTypeCode = messageTypeCode,
                    HandlerTypeCode = handlerTypeCode,
                    Timestamp = DateTime.Now
                };
                var sequenceMessage = message as ISequenceMessage;
                if (sequenceMessage != null)
                {
                    messageHandleRecord.AggregateRootTypeCode = sequenceMessage.AggregateRootTypeCode;
                    messageHandleRecord.AggregateRootId = sequenceMessage.AggregateRootId;
                    messageHandleRecord.Version = sequenceMessage.Version;
                }

                AddMessageHandledRecordAsync(singleMessageDispatching, messageHandleRecord, handlerProxy.GetInnerHandler().GetType(), handlerTypeCode, handlerProxy, queueHandler, 0);
            },
            () => string.Format("[messageId:{0}, messageType:{1}, handlerType:{2}]", message.Id, message.GetType().Name, handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }
        private void HandleTwoMessageAsync(MultiMessageDisptaching multiMessageDispatching, IMessageHandlerProxy2 handlerProxy, int handlerTypeCode, QueuedHandler<IMessageHandlerProxy2> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;
            var message1 = messages[0];
            var message2 = messages[1];

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("HandleTwoMessageAsync",
            () => handlerProxy.HandleAsync(message1, message2),
            currentRetryTimes => HandleTwoMessageAsync(multiMessageDispatching, handlerProxy, handlerTypeCode, queueHandler, currentRetryTimes),
            result =>
            {
                var message1TypeCode = _typeCodeProvider.GetTypeCode(message1.GetType());
                var message2TypeCode = _typeCodeProvider.GetTypeCode(message2.GetType());
                var messageHandleRecord = new TwoMessageHandleRecord
                {
                    MessageId1 = message1.Id,
                    MessageId2 = message2.Id,
                    Message1TypeCode = message1TypeCode,
                    Message2TypeCode = message2TypeCode,
                    HandlerTypeCode = handlerTypeCode,
                    Timestamp = DateTime.Now
                };
                var sequenceMessage = message1 as ISequenceMessage;
                if (sequenceMessage != null)
                {
                    messageHandleRecord.AggregateRootTypeCode = sequenceMessage.AggregateRootTypeCode;
                    messageHandleRecord.AggregateRootId = sequenceMessage.AggregateRootId;
                    messageHandleRecord.Version = sequenceMessage.Version;
                }

                AddTwoMessageHandledRecordAsync(multiMessageDispatching, messageHandleRecord, handlerTypeCode, handlerProxy, queueHandler, 0);
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }
        private void HandleThreeMessageAsync(MultiMessageDisptaching multiMessageDispatching, IMessageHandlerProxy3 handlerProxy, int handlerTypeCode, QueuedHandler<IMessageHandlerProxy3> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;
            var message1 = messages[0];
            var message2 = messages[1];
            var message3 = messages[2];

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("HandleTwoMessageAsync",
            () => handlerProxy.HandleAsync(message1, message2, message3),
            currentRetryTimes => HandleThreeMessageAsync(multiMessageDispatching, handlerProxy, handlerTypeCode, queueHandler, currentRetryTimes),
            result =>
            {
                var message1TypeCode = _typeCodeProvider.GetTypeCode(message1.GetType());
                var message2TypeCode = _typeCodeProvider.GetTypeCode(message2.GetType());
                var message3TypeCode = _typeCodeProvider.GetTypeCode(message3.GetType());
                var messageHandleRecord = new ThreeMessageHandleRecord
                {
                    MessageId1 = message1.Id,
                    MessageId2 = message2.Id,
                    MessageId3 = message3.Id,
                    Message1TypeCode = message1TypeCode,
                    Message2TypeCode = message2TypeCode,
                    Message3TypeCode = message3TypeCode,
                    HandlerTypeCode = handlerTypeCode,
                    Timestamp = DateTime.Now
                };
                var sequenceMessage = message1 as ISequenceMessage;
                if (sequenceMessage != null)
                {
                    messageHandleRecord.AggregateRootTypeCode = sequenceMessage.AggregateRootTypeCode;
                    messageHandleRecord.AggregateRootId = sequenceMessage.AggregateRootId;
                    messageHandleRecord.Version = sequenceMessage.Version;
                }

                AddThreeMessageHandledRecordAsync(multiMessageDispatching, messageHandleRecord, handlerTypeCode, handlerProxy, queueHandler, 0);
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }

        private void AddMessageHandledRecordAsync(SingleMessageDisptaching singleMessageDispatching, MessageHandleRecord messageHandleRecord, Type handlerType, int handlerTypeCode, IMessageHandlerProxy1 handlerProxy, QueuedHandler<IMessageHandlerProxy1> queueHandler, int retryTimes)
        {
            var message = singleMessageDispatching.Message;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("AddMessageHandledRecordAsync",
            () => _messageHandleRecordStore.AddRecordAsync(messageHandleRecord),
            currentRetryTimes => AddMessageHandledRecordAsync(singleMessageDispatching, messageHandleRecord, handlerType, handlerTypeCode, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                singleMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                if (queueHandler != null)
                {
                    queueHandler.OnHandlerFinished(handlerProxy);
                }
                _logger.DebugFormat("Message handled success, handlerType:{0}, messageType:{1}, messageId:{2}", handlerType.Name, message.GetType().Name, message.Id);
            },
            () => string.Format("[messageId:{0}, messageType:{1}, handlerType:{2}]", message.Id, message.GetType().Name, handlerType.Name),
            null,
            retryTimes,
            true);
        }
        private void AddTwoMessageHandledRecordAsync(MultiMessageDisptaching multiMessageDispatching, TwoMessageHandleRecord messageHandleRecord, int handlerTypeCode, IMessageHandlerProxy2 handlerProxy, QueuedHandler<IMessageHandlerProxy2> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("AddTwoMessageHandledRecordAsync",
            () => _messageHandleRecordStore.AddRecordAsync(messageHandleRecord),
            currentRetryTimes => AddTwoMessageHandledRecordAsync(multiMessageDispatching, messageHandleRecord, handlerTypeCode, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                multiMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                if (queueHandler != null)
                {
                    queueHandler.OnHandlerFinished(handlerProxy);
                }
                _logger.DebugFormat("TwoMessage handled success, [messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name);
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }
        private void AddThreeMessageHandledRecordAsync(MultiMessageDisptaching multiMessageDispatching, ThreeMessageHandleRecord messageHandleRecord, int handlerTypeCode, IMessageHandlerProxy3 handlerProxy, QueuedHandler<IMessageHandlerProxy3> queueHandler, int retryTimes)
        {
            var messages = multiMessageDispatching.Messages;

            _ioHelper.TryAsyncActionRecursively<AsyncTaskResult>("AddThreeMessageHandledRecordAsync",
            () => _messageHandleRecordStore.AddRecordAsync(messageHandleRecord),
            currentRetryTimes => AddThreeMessageHandledRecordAsync(multiMessageDispatching, messageHandleRecord, handlerTypeCode, handlerProxy, queueHandler, currentRetryTimes),
            result =>
            {
                multiMessageDispatching.RemoveHandledHandler(handlerTypeCode);
                if (queueHandler != null)
                {
                    queueHandler.OnHandlerFinished(handlerProxy);
                }
                _logger.DebugFormat("ThreeMessage handled success, [messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name);
            },
            () => string.Format("[messages:[{0}], handlerType:{1}]", string.Join("|", messages.Select(x => string.Format("id:{0},type:{1}", x.Id, x.GetType().Name))), handlerProxy.GetInnerHandler().GetType().Name),
            null,
            retryTimes,
            true);
        }

        #endregion

        #region Private Classes

        class RootDisptaching
        {
            private TaskCompletionSource<AsyncTaskResult> _taskCompletionSource;
            private ConcurrentDictionary<object, bool> _childDispatchingDict;

            public Task<AsyncTaskResult> Task
            {
                get { return _taskCompletionSource.Task; }
            }

            public RootDisptaching()
            {
                _taskCompletionSource = new TaskCompletionSource<AsyncTaskResult>();
                _childDispatchingDict = new ConcurrentDictionary<object, bool>();
            }

            public void AddChildDispatching(object childDispatching)
            {
                _childDispatchingDict.TryAdd(childDispatching, false);
            }
            public void OnChildDispatchingFinished(object childDispatching)
            {
                bool removed;
                if (_childDispatchingDict.TryRemove(childDispatching, out removed))
                {
                    if (_childDispatchingDict.IsEmpty)
                    {
                        _taskCompletionSource.SetResult(AsyncTaskResult.Success);
                    }
                }
            }
        }
        class QueueMessageDisptaching
        {
            private DefaultMessageDispatcher _dispatcher;
            private RootDisptaching _rootDispatching;
            private ConcurrentQueue<IMessage> _messageQueue;

            public QueueMessageDisptaching(DefaultMessageDispatcher dispatcher, RootDisptaching rootDispatching, IEnumerable<IMessage> messages)
            {
                _dispatcher = dispatcher;
                _messageQueue = new ConcurrentQueue<IMessage>();
                messages.ForEach(message => _messageQueue.Enqueue(message));
                _rootDispatching = rootDispatching;
                _rootDispatching.AddChildDispatching(this);
            }

            public IMessage DequeueMessage()
            {
                IMessage message;
                if (_messageQueue.TryDequeue(out message))
                {
                    return message;
                }
                return null;
            }
            public void OnMessageHandled(IMessage message)
            {
                var nextMessage = DequeueMessage();
                if (nextMessage == null)
                {
                    _rootDispatching.OnChildDispatchingFinished(this);
                    return;
                }
                _dispatcher.DispatchSingleMessage(nextMessage, this);
            }
        }
        class MultiMessageDisptaching
        {
            private IMessage[] _messages;
            private ConcurrentDictionary<int, IHandlerProxy> _handlerDict;
            private RootDisptaching _rootDispatching;

            public IMessage[] Messages
            {
                get { return _messages; }
            }

            public MultiMessageDisptaching(IEnumerable<IMessage> messages, IEnumerable<IHandlerProxy> handlers, RootDisptaching rootDispatching, ITypeCodeProvider typeCodeProvider)
            {
                _messages = messages.ToArray();
                _handlerDict = new ConcurrentDictionary<int, IHandlerProxy>();
                handlers.ForEach(x => _handlerDict.TryAdd(typeCodeProvider.GetTypeCode(x.GetInnerHandler().GetType()), x));
                _rootDispatching = rootDispatching;
                _rootDispatching.AddChildDispatching(this);
            }

            public void RemoveHandledHandler(int handlerTypeCode)
            {
                IHandlerProxy handler;
                if (_handlerDict.TryRemove(handlerTypeCode, out handler))
                {
                    if (_handlerDict.IsEmpty)
                    {
                        _rootDispatching.OnChildDispatchingFinished(this);
                    }
                }
            }
        }
        class SingleMessageDisptaching
        {
            private ConcurrentDictionary<int, IHandlerProxy> _handlerDict;
            private QueueMessageDisptaching _queueMessageDispatching;

            public IMessage Message { get; private set; }

            public SingleMessageDisptaching(IMessage message, QueueMessageDisptaching queueMessageDispatching, IEnumerable<IHandlerProxy> handlers, ITypeCodeProvider typeCodeProvider)
            {
                Message = message;
                _queueMessageDispatching = queueMessageDispatching;
                _handlerDict = new ConcurrentDictionary<int, IHandlerProxy>();
                handlers.ForEach(x => _handlerDict.TryAdd(typeCodeProvider.GetTypeCode(x.GetInnerHandler().GetType()), x));
            }

            public void RemoveHandledHandler(int handlerTypeCode)
            {
                IHandlerProxy handler;
                if (_handlerDict.TryRemove(handlerTypeCode, out handler))
                {
                    if (_handlerDict.IsEmpty)
                    {
                        _queueMessageDispatching.OnMessageHandled(Message);
                    }
                }
            }
        }
        class QueuedHandler<T> where T : class, IHandlerProxy
        {
            private Action<QueuedHandler<T>, T> _dispatchToNextHandler;
            private ConcurrentQueue<T> _handlerQueue;

            public QueuedHandler(IEnumerable<T> handlers, Action<QueuedHandler<T>, T> dispatchToNextHandler)
            {
                _handlerQueue = new ConcurrentQueue<T>();
                handlers.ForEach(handler => _handlerQueue.Enqueue(handler));
                _dispatchToNextHandler = dispatchToNextHandler;
            }

            public T DequeueHandler()
            {
                T handler;
                if (_handlerQueue.TryDequeue(out handler))
                {
                    return handler;
                }
                return null;
            }
            public void OnHandlerFinished(T handler)
            {
                var nextHandler = DequeueHandler();
                if (nextHandler != null)
                {
                    _dispatchToNextHandler(this, nextHandler);
                }
            }
        }

        #endregion
    }
}
