// Minimal game boundaries for exercising the real runtime without launching SE.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Shared.Config;

namespace Sandbox
{
    public static class MySandboxGame { public static bool IsPaused; }
}
namespace Sandbox.Game.Multiplayer
{
    public static class Sync { public static bool IsServer = true; }
}
namespace Sandbox.Game.World
{
    public sealed class MySession
    {
        public static MySession Static;
        public static event Action BeforeLoading;
        public static event Action OnUnloading;
        public static void Loading() => BeforeLoading?.Invoke();
        public bool Ready;
        public bool IsUnloading;
        public string CurrentPath;
        public static void Unload()
        {
            Static.IsUnloading = true;
            OnUnloading?.Invoke();
            Static = null;
        }
    }
}
namespace VRageMath
{
    public struct Vector3D { }
    public struct BoundingSphereD
    {
        public double Radius;
        public BoundingSphereD(Vector3D center, double radius) { Radius = radius; }
    }
}
namespace Shared.Logging
{
    public interface IPluginLogger
    {
        void Critical(Exception ex, string message);
        void Error(Exception ex, string message, params object[] data);
        void Info(string message, params object[] data);
    }
    public sealed class TestLogger : IPluginLogger
    {
        public int Failures;
        public void Critical(Exception ex, string message) { Failures++; }
        public void Error(Exception ex, string message, params object[] data) { Failures++; }
        public void Info(string message, params object[] data) { }
    }
}
namespace Shared.Concealment
{
    public sealed class ConcealmentManager : IDisposable
    {
        public static int Created;
        public bool Disposed, Stopped;
        public bool RequiresWorldRetention;
        public int Updates;
        public int Reveals;
        public int Scans;
        public double Distance;
        public bool ThrowOnUpdate;
        public bool ThrowOnReveal;
        public bool ThrowOnDispose;
        public ConcealmentManager(ConcealmentRuntime runtime, Harmony harmony)
        {
            Created++;
            harmony.Patch(AccessTools.Method(typeof(ConcealmentManager), nameof(PatchTarget)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConcealmentManager), nameof(Prefix))));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void PatchTarget() { }
        private static void Prefix() { }
        public void Update()
        {
            Updates++;
            if (ThrowOnUpdate) throw new InvalidOperationException("update failed");
        }
        public int RevealAll()
        {
            Reveals++;
            if (ThrowOnReveal) throw new InvalidOperationException("reveal failed");
            return 3;
        }
        public int ConcealGrids(double distance) { Scans++; Distance = distance; return 2; }
        public int RevealGridsInSphere(VRageMath.BoundingSphereD sphere) { Distance = sphere.Radius; return 1; }
        public void StopScheduling() { Stopped = true; }
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("dispose failed");
        }
    }
}
