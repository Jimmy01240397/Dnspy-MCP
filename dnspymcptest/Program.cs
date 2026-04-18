using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DnSpyMcp.TestTarget;

internal static class Program
{
    // Static fields — for list_static_fields / read_memory tests.
    public static int TickCounter;
    public static string StateLabel = "initial";
    public static readonly List<Widget> AliveWidgets = new();

    private static void Main(string[] args)
    {
        Console.WriteLine($"dnspymcptest PID={Process.GetCurrentProcess().Id}");
        Console.WriteLine($"  CLR  : {Environment.Version}");
        Console.WriteLine($"  args : [{string.Join(", ", args)}]");
        Console.WriteLine("Ready. Waiting for debugger. Ctrl+C to quit.");

        // Allocate some objects up front so heap tools have something to find.
        for (int i = 0; i < 10; i++)
            AliveWidgets.Add(new Widget($"widget-{i}", i * 7));

        var loopCancel = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; loopCancel.Set(); };

        while (!loopCancel.IsSet)
        {
            TickCounter++;
            StateLabel = TickCounter % 2 == 0 ? "even" : "odd";
            var result = Compute(TickCounter, TickCounter * 3);
            if (TickCounter % 10 == 0)
                Console.WriteLine($"[tick {TickCounter}] state={StateLabel} compute={result} widgets={AliveWidgets.Count}");
            if (TickCounter % 50 == 0)
                Churn();
            Thread.Sleep(500);
        }

        Console.WriteLine("bye.");
    }

    // Deliberately simple call chain for breakpoint / step / clrstack tests.
    private static int Compute(int a, int b)
    {
        var sum = Add(a, b);
        var prod = Multiply(a, b);
        return sum + prod;
    }

    private static int Add(int a, int b) => a + b;

    private static int Multiply(int a, int b)
    {
        int acc = 0;
        for (int i = 0; i < b; i++)
            acc += a;
        return acc;
    }

    // Allocate + release to exercise GC / heap_stats.
    private static void Churn()
    {
        var tmp = new List<Widget>();
        for (int i = 0; i < 100; i++)
            tmp.Add(new Widget($"churn-{i}", i));
        tmp.Clear();
    }
}

public sealed class Widget
{
    public string Name { get; }
    public int Value { get; set; }
    public DateTime CreatedAt { get; }

    public Widget(string name, int value)
    {
        Name = name;
        Value = value;
        CreatedAt = DateTime.UtcNow;
    }

    public override string ToString() => $"Widget({Name}, {Value})";
}
