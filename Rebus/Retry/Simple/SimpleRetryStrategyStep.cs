﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Transport;

namespace Rebus.Retry.Simple
{
    public class SimpleRetryStrategyStep : IIncomingStep
    {
        static readonly TimeSpan MoveToErrorQueueFailedPause = TimeSpan.FromSeconds(5);
        static ILog _log;

        static SimpleRetryStrategyStep()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly ConcurrentDictionary<string, ErrorTracking> _trackedErrors = new ConcurrentDictionary<string, ErrorTracking>();
        readonly SimpleRetryStrategySettings _simpleRetryStrategySettings;
        readonly ITransport _transport;

        public SimpleRetryStrategyStep(ITransport transport, SimpleRetryStrategySettings simpleRetryStrategySettings)
        {
            _transport = transport;
            _simpleRetryStrategySettings = simpleRetryStrategySettings;
        }

        public async Task Process(IncomingStepContext context, Func<Task> next)
        {
            var transportMessage = context.Load<TransportMessage>();
            var transactionContext = context.Load<ITransactionContext>();
            var messageId = transportMessage.Headers.GetValueOrNull(Headers.MessageId);

            if (string.IsNullOrWhiteSpace(messageId))
            {
                await MoveMessageToErrorQueue("<no message ID>", transportMessage, 
                    string.Format("Received message with empty or absent '{0}' header! All messages must be" +
                                  " supplied with an ID . If no ID is present, the message cannot be tracked" +
                                  " between delivery attempts, and other stuff would also be much harder to" +
                                  " do - therefore, it is a requirement that messages be supplied with an ID.",
                    Headers.MessageId),
                    transactionContext);

                return;
            }

            if (HasFailedTooManyTimes(messageId))
            {
                await MoveMessageToErrorQueue(messageId, transportMessage, GetErrorDescriptionFor(messageId), transactionContext);
                
                return;
            }

            try
            {
                await next();
            }
            catch (Exception exception)
            {
                var errorTracking = _trackedErrors.AddOrUpdate(messageId,
                    id => new ErrorTracking(exception),
                    (id, tracking) => tracking.AddError(exception));

                _log.Warn("Unhandled exception {0} while handling message with ID {1}: {2}",
                    errorTracking.Errors.Count(), messageId, exception);

                transactionContext.Abort();
            }
        }

        string GetErrorDescriptionFor(string messageId)
        {
            ErrorTracking errorTracking;
            if (!_trackedErrors.TryRemove(messageId, out errorTracking))
            {
                return "Could not get error details for the message";
            }

            return string.Join(Environment.NewLine,
                errorTracking.Errors.Select(c => string.Format("{0}: {1}", c.Time, c.Exception)));
        }

        async Task MoveMessageToErrorQueue(string messageId, TransportMessage transportMessage, string errorDescription, ITransactionContext transactionContext)
        {
            var headers = transportMessage.Headers;

            headers[Headers.ErrorDetails] = errorDescription;
            headers[Headers.SourceQueue] = _transport.Address;

            var moveToErrorQueueFailed = false;
            var errorQueueAddress = _simpleRetryStrategySettings.ErrorQueueAddress;

            try
            {
                _log.Error("Moving message with ID {0} to error queue '{1}' - reason: {2}", messageId, errorQueueAddress, errorDescription);

                await _transport.Send(errorQueueAddress, transportMessage, transactionContext);
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Could not move message with ID {0} to error queue '{1}' - will pause {2} to avoid thrashing", 
                    messageId, errorQueueAddress, MoveToErrorQueueFailedPause);

                moveToErrorQueueFailed = true;
            }

            // if we can't move to error queue, we need to avoid thrashing over and over
            if (moveToErrorQueueFailed)
            {
                await Task.Delay(MoveToErrorQueueFailedPause);
            }
        }

        bool HasFailedTooManyTimes(string messageId)
        {
            ErrorTracking existingTracking;
            var hasTrackingForThisMessage = _trackedErrors.TryGetValue(messageId, out existingTracking);
            
            if (!hasTrackingForThisMessage) return false;

            var hasFailedTooManyTimes = existingTracking.ErrorCount >= _simpleRetryStrategySettings.MaxDeliveryAttempts;

            return hasFailedTooManyTimes;
        }

        class ErrorTracking
        {
            readonly List<CaughtException> _caughtExceptions = new List<CaughtException>();

            public ErrorTracking(Exception exception)
            {
                AddError(exception);
            }

            public int ErrorCount
            {
                get { return _caughtExceptions.Count; }
            }

            public IEnumerable<CaughtException> Errors
            {
                get { return _caughtExceptions; }
            }

            public ErrorTracking AddError(Exception caughtException)
            {
                _caughtExceptions.Add(new CaughtException(caughtException));
                return this;
            }
        }

        class CaughtException
        {
            public CaughtException(Exception exception)
            {
                Exception = exception;
                Time = DateTime.UtcNow;
            }

            public Exception Exception { get; set; }
            public DateTime Time { get; set; }
        }
    }
}