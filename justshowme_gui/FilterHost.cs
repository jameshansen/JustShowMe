using System;
using System.Linq;
using System.Reflection;
using JustShowMe.Filter;

namespace JustShowMe
{
    /// Loads the configured filter DLL by path and returns its IFrameFilter.
    /// Default is justshowme_filter.dll beside the exe, but the user can point
    /// the ini at any DLL that implements IFrameFilter.
    public static class FilterHost
    {
        public static IFrameFilter Load(string dllPath)
        {
            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetTypes().FirstOrDefault(t =>
                typeof(IFrameFilter).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (type == null)
                throw new InvalidOperationException(
                    $"No IFrameFilter implementation found in {dllPath}.");
            return (IFrameFilter)Activator.CreateInstance(type);
        }
    }
}
