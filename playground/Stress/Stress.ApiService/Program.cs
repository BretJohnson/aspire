// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Stress.ApiService;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(TraceCreator.ActivitySourceName));

var app = builder.Build();

app.Lifetime.ApplicationStarted.Register(ConsoleStresser.Stress);

app.MapGet("/", () => "Hello world");

app.MapGet("/big-trace", async () =>
{
    var bigTraceCreator = new TraceCreator();

    await bigTraceCreator.CreateTraceAsync(count: 10, createChildren: true);

    return "Big trace created";
});

app.MapGet("/trace-limit", async () =>
{
    const int TraceCount = 20_000;

    var current = Activity.Current;
    Activity.Current = null;
    var bigTraceCreator = new TraceCreator();

    for (var i = 0; i < TraceCount; i++)
    {
        await bigTraceCreator.CreateTraceAsync(count: 1, createChildren: false);
    }

    Activity.Current = current;

    return $"Created {TraceCount} traces.";
});

app.MapGet("/log-message-limit", ([FromServices] ILogger<Program> logger) =>
{
    const int LogCount = 20_000;

    for (var i = 0; i < LogCount; i++)
    {
        logger.LogInformation("Log entry {LogEntryIndex}", i);
    }

    return $"Created {LogCount} logs.";
});

app.MapGet("/log-message", ([FromServices] ILogger<Program> logger) =>
{
    const string message = "Hello World";
    IReadOnlyList<KeyValuePair<string, object?>> eventData = new List<KeyValuePair<string, object?>>()
    {
        new KeyValuePair<string, object?>("Message", message),
        new KeyValuePair<string, object?>("ActivityId", 123),
        new KeyValuePair<string, object?>("Level", (int) 10),
        new KeyValuePair<string, object?>("Tid", Environment.CurrentManagedThreadId),
        new KeyValuePair<string, object?>("Pid", Environment.ProcessId),
    };

    logger.Log(LogLevel.Information, 0, eventData, null, formatter: (_, _) => null!);

    return message;
});

app.MapGet("/many-logs", (ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    var channel = Channel.CreateUnbounded<string>();
    var logger = loggerFactory.CreateLogger("ManyLogs");

    cancellationToken.Register(() =>
    {
        logger.LogInformation("Writing logs canceled.");
    });

    // Write logs for 1 minute.
    _ = Task.Run(async () =>
    {
        var stopwatch = Stopwatch.StartNew();
        var logCount = 0;
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            logCount++;
            logger.LogInformation("This is log message {LogCount}.", logCount);

            if (logCount % 100 == 0)
            {
                channel.Writer.TryWrite($"Logged {logCount} messages.");
            }

            await Task.Delay(5, cancellationToken);
        }

        channel.Writer.Complete();
    }, cancellationToken);

    return WriteOutput();

    async IAsyncEnumerable<string> WriteOutput()
    {
        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }
});

app.Run();
