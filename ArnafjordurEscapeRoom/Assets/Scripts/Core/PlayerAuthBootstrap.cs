using System;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace Arnafjordur.Core
{
    /// <summary>
    /// Initializes Unity Services and ensures anonymous player authentication non-blocking at boot.
    /// </summary>
    public static class PlayerAuthBootstrap
    {
        private static Task _inFlightInit;

        public static bool IsReady =>
            UnityServices.State == ServicesInitializationState.Initialized &&
            AuthenticationService.Instance.IsSignedIn;

        public static string PlayerId =>
            IsReady ? AuthenticationService.Instance.PlayerId : string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad()
        {
            _ = EnsureSignedInAsync();
        }

        public static async Task EnsureSignedInAsync()
        {
            if (IsReady)
            {
                return;
            }

            _inFlightInit ??= InitializeAndSignInInternalAsync();
            try
            {
                await _inFlightInit;
            }
            finally
            {
                if (!IsReady)
                {
                    _inFlightInit = null;
                }
            }
        }

        private static async Task InitializeAndSignInInternalAsync()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    await UnityServices.InitializeAsync();
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }

                Debug.Log($"[PlayerAuthBootstrap] Signed in anonymously as {AuthenticationService.Instance.PlayerId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerAuthBootstrap] Cloud sign-in deferred or failed: {ex.Message}");
            }
        }
    }
}
