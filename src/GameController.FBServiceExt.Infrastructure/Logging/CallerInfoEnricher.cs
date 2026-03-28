using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace GameController.FBServiceExt.Infrastructure.Logging;

public sealed class CallerInfoEnricher : ILogEventEnricher
{
    private readonly string _namespacePrefix;

    public CallerInfoEnricher(string namespacePrefix)
    {
        _namespacePrefix = namespacePrefix;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var frame = FindRelevantFrame();
        if (frame is null)
        {
            return;
        }

        var method = frame.GetMethod();
        var declaringType = method?.DeclaringType;
        if (method is null || declaringType is null)
        {
            return;
        }

        var typeName = TrimNamespacePrefix(declaringType.FullName ?? declaringType.Name);
        var memberName = method.Name;
        var lineNumber = frame.GetFileLineNumber();

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CallerTypeName", typeName));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CallerMemberName", memberName));

        if (lineNumber > 0)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CallerLineNumber", lineNumber));
        }
    }

    private StackFrame? FindRelevantFrame()
    {
        var trace = new StackTrace(true);
        foreach (var frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            var declaringType = method?.DeclaringType;
            var fullName = declaringType?.FullName;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            if (!fullName.StartsWith(_namespacePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (fullName.StartsWith($"{_namespacePrefix}.Infrastructure.Logging", StringComparison.Ordinal))
            {
                continue;
            }

            return frame;
        }

        return null;
    }

    private string TrimNamespacePrefix(string fullName)
    {
        return fullName.StartsWith(_namespacePrefix + ".", StringComparison.Ordinal)
            ? fullName[(_namespacePrefix.Length + 1)..]
            : fullName;
    }
}