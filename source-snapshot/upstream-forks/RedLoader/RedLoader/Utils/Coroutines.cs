using System;
using System.Collections;
using RedLoader.Unity.IL2CPP.UnityEngine;
using RedLoader.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace RedLoader
{
    internal interface ICoroutineDriver
    {
        Coroutine Start(IEnumerator routine);
        void Stop(Coroutine coroutineToken);
    }

    public class Coroutines
    {
        private static ICoroutineDriver driver;

        internal static bool HasDriver => driver != null;

        internal static void SetDriver(ICoroutineDriver value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(driver, value))
                return;
            if (driver != null)
                throw new InvalidOperationException("Coroutine dispatch ownership has already been selected.");

            driver = value;
        }

        /// <summary>
        /// Start a new coroutine.<br />
        /// Coroutines are called at the end of the game Update loops.
        /// </summary>
        /// <param name="routine">The target routine</param>
        /// <returns>An object that can be passed to Stop to stop this coroutine</returns>
        public static Coroutine Start(IEnumerator routine)
        {
            if (driver != null)
                return driver.Start(routine);

            if (GlobalBehaviour.Instance == null)
                throw new NotSupportedException("Support module must be initialized before starting coroutines");
            return GlobalBehaviour.Instance.StartCoroutine(routine.WrapToIl2Cpp());
        }

        /// <summary>
        /// Stop a currently running coroutine
        /// </summary>
        /// <param name="coroutineToken">The coroutine to stop</param>
        public static void Stop(Coroutine coroutineToken)
        {
            if (driver != null)
            {
                driver.Stop(coroutineToken);
                return;
            }

            if (GlobalBehaviour.Instance == null)
                throw new NotSupportedException("Support module must be initialized before starting coroutines");
            GlobalBehaviour.Instance.StopCoroutine(coroutineToken);
        }
        
        public class CoroutineToken
        {
            private Coroutine _token;
            
            public bool IsValid => _token != null;

            public CoroutineToken(Coroutine token)
            {
                _token = token;
            }

            public void Stop()
            {
                if (!IsValid)
                    return;

                Coroutines.Stop(_token);
                _token = null;
            }

            public static implicit operator bool(CoroutineToken token) => token?.IsValid ?? false;
        }
    }

    public static class CoroutineExtensions
    {
        public static Coroutines.CoroutineToken RunCoro(this IEnumerator coro)
        {
            return new Coroutines.CoroutineToken(Coroutines.Start(coro));
        }
    }
}
