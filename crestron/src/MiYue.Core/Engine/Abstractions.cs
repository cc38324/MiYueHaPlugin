using System;
using System.Collections.Generic;

namespace MiYue.Core.Engine
{
    /// <summary>One HTTP request handed to the platform transport.</summary>
    public sealed class HttpRequestData
    {
        public string Method = "GET";
        public string Url = string.Empty;
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        /// <summary>Whole-transfer timeout.</summary>
        public int TimeoutMs = 6000;
    }

    /// <summary>Result of one HTTP transfer.</summary>
    public sealed class HttpResponseData
    {
        /// <summary>False when no HTTP response arrived (connect failure, timeout, reset).</summary>
        public bool Completed;
        public int Status;
        public byte[] Body;
        public string Error = string.Empty;

        public static HttpResponseData Failed(string error)
        {
            return new HttpResponseData { Completed = false, Error = error ?? "transport error" };
        }
    }

    /// <summary>
    /// Platform HTTP transport. Implementations MUST be non-blocking for the caller and MUST invoke
    /// <paramref name="done"/> exactly once, on any thread (the engine re-posts it onto its own strand).
    /// Crestron: MiYue.Crestron.CrestronHttpTransport (Crestron.SimplSharp.Net.Http, DispatchAsync).
    /// </summary>
    public interface IHttpTransport
    {
        void Send(HttpRequestData request, Action<HttpResponseData> done);
    }

    /// <summary>A cancellable one-shot timer.</summary>
    public interface ITimerHandle
    {
        void Cancel();
    }

    /// <summary>
    /// Platform timers + clock. <see cref="Schedule"/> fires the callback once after dueMs on any thread.
    /// Crestron: CTimer. Tests: a manual virtual clock.
    /// </summary>
    public interface IScheduler
    {
        ITimerHandle Schedule(int dueMs, Action callback);
        long NowMs { get; }
    }

    /// <summary>Runs work off the caller's thread (Crestron: CrestronInvoke.BeginInvoke; tests: inline).</summary>
    public interface IWorkDispatcher
    {
        void Dispatch(Action work);
    }

    /// <summary>Debug / error logging (Crestron: CrestronConsole + ErrorLog).</summary>
    public interface ILog
    {
        void Debug(string message);
        void Error(string message);
    }

    /// <summary>Receives changed output values (already de-duplicated by <see cref="OutputCache"/>).</summary>
    public interface IOutputSink
    {
        void Digital(ushort index, bool value);
        void Analog(ushort index, ushort value);
        void Serial(ushort index, string value);
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        public void Debug(string message)
        {
        }

        public void Error(string message)
        {
        }
    }

    public sealed class InlineDispatcher : IWorkDispatcher
    {
        public static readonly InlineDispatcher Instance = new InlineDispatcher();

        public void Dispatch(Action work)
        {
            work();
        }
    }
}
