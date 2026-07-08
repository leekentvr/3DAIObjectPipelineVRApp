using System.Runtime.CompilerServices;
using UnityEngine.Networking;

namespace Pipeline
{
    /// <summary>
    /// Lets you `await someUnityWebRequest.SendWebRequest();` instead of hand-rolling a
    /// coroutine for every single API call. Continuations run on Unity's main-thread
    /// SynchronizationContext, so it's safe to touch GameObjects/Transforms right after
    /// an awaited request.
    /// </summary>
    public static class UnityWebRequestExtensions
    {
        public static UnityWebRequestAwaiter GetAwaiter(this UnityWebRequestAsyncOperation op)
        {
            return new UnityWebRequestAwaiter(op);
        }

        public readonly struct UnityWebRequestAwaiter : INotifyCompletion
        {
            private readonly UnityWebRequestAsyncOperation _op;

            public UnityWebRequestAwaiter(UnityWebRequestAsyncOperation op) => _op = op;

            public bool IsCompleted => _op.isDone;

            public UnityWebRequest GetResult() => _op.webRequest;

            public void OnCompleted(System.Action continuation)
            {
                _op.completed += _ => continuation();
            }
        }
    }
}
