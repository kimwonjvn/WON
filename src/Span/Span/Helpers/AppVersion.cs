using System;
using System.Reflection;

namespace Span.Helpers;

/// <summary>앱 버전. 패키지 매니페스트가 있으면 그것을, 없으면(패키지 없이 설치) 어셈블리 버전을 쓴다.</summary>
public static class AppVersion
{
    public static Version Current { get; } = Resolve();

    private static Version Resolve()
    {
        try
        {
            if (PackageIdentity.IsPackaged)
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return new Version(v.Major, v.Minor, v.Build, v.Revision);
            }
        }
        catch { /* 아래 어셈블리 버전으로 */ }
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }
}
