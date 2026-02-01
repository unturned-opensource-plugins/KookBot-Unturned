using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using SDG.Unturned;

namespace Emqo.KookBot_Unturned
{
    internal static class CommandOutputInterceptor
    {
        private static readonly object SyncRoot = new();
        private static readonly List<string> Buffer = new();
        private static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromMilliseconds(120);

        private static readonly EventInfo TargetEvent;
        private static Delegate _subscription;
        private static int _subscriberCount;

        static CommandOutputInterceptor()
        {
            TargetEvent = ResolveEvent();
        }

        private static EventInfo ResolveEvent()
        {
            try
            {
                var type = typeof(CommandWindow);
                return type
                    .GetEvents(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(IsCompatibleEvent);
            }
            catch (Exception ex)
            {
                Rocket.Core.Logging.Logger.LogError($"Error resolving CommandWindow event: {ex.Message}");
                return null;
            }
        }

        private static bool IsCompatibleEvent(EventInfo eventInfo)
        {
            var handlerType = eventInfo.EventHandlerType;
            var invoke = handlerType?.GetMethod("Invoke");
            if (invoke == null)
            {
                return false;
            }

            var parameters = invoke.GetParameters();
            return parameters.Any(p => p.ParameterType == typeof(string));
        }

        public static async Task<string> CaptureAsync(Func<Task> action, TimeSpan? flushDelay = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var subscribed = EnsureSubscribed();
            try
            {
                await action();

                if (!subscribed)
                {
                    return null;
                }

                await Task.Delay(flushDelay ?? DefaultFlushDelay);
                return ExtractOutput();
            }
            finally
            {
                if (subscribed)
                {
                    ReleaseSubscription();
                }
            }
        }

        private static bool EnsureSubscribed()
        {
            if (TargetEvent == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                _subscriberCount++;
                if (_subscriberCount > 1)
                {
                    return true;
                }

                Buffer.Clear();
                _subscription = CreateHandler(TargetEvent);
                if (_subscription == null)
                {
                    _subscriberCount = 0;
                    return false;
                }

                try
                {
                    TargetEvent.AddEventHandler(null, _subscription);
                    return true;
                }
                catch (Exception ex)
                {
                    Rocket.Core.Logging.Logger.LogError($"Error adding event handler: {ex.Message}");
                    _subscription = null;
                    _subscriberCount = 0;
                    return false;
                }
            }
        }

        private static void ReleaseSubscription()
        {
            lock (SyncRoot)
            {
                _subscriberCount--;
                if (_subscriberCount > 0)
                {
                    return;
                }

                _subscriberCount = 0;
                if (_subscription == null)
                {
                    return;
                }

                try
                {
                    TargetEvent?.RemoveEventHandler(null, _subscription);
                }
                catch (Exception ex)
                {
                    Rocket.Core.Logging.Logger.LogError($"Error removing event handler: {ex.Message}");
                }
                finally
                {
                    _subscription = null;
                    Buffer.Clear();
                }
            }
        }

        private static Delegate CreateHandler(EventInfo eventInfo)
        {
            try
            {
                var handlerType = eventInfo.EventHandlerType;
                var invoke = handlerType?.GetMethod("Invoke");
                if (invoke == null)
                {
                    return null;
                }

                var parameters = invoke.GetParameters()
                    .Select(p => Expression.Parameter(p.ParameterType))
                    .ToArray();

                var stringParameter = parameters.FirstOrDefault(p => p.Type == typeof(string));
                if (stringParameter == null)
                {
                    return null;
                }

                var handleMethod = typeof(CommandOutputInterceptor).GetMethod(
                    nameof(HandleOutput),
                    BindingFlags.NonPublic | BindingFlags.Static);

                var body = Expression.Call(handleMethod, stringParameter);
                return Expression.Lambda(handlerType, body, parameters).Compile();
            }
            catch (Exception ex)
            {
                Rocket.Core.Logging.Logger.LogError($"Error creating event handler: {ex.Message}");
                return null;
            }
        }

        private static void HandleOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (SyncRoot)
            {
                Buffer.Add(line.TrimEnd());
            }
        }

        private static string ExtractOutput()
        {
            lock (SyncRoot)
            {
                if (Buffer.Count == 0)
                {
                    return null;
                }

                var text = string.Join(Environment.NewLine, Buffer);
                Buffer.Clear();
                return text;
            }
        }
    }
}

