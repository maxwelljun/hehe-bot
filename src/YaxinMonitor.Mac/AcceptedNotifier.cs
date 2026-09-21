using System.Diagnostics;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed class AcceptedNotifier
{
    private readonly HashSet<string> _notifiedOrders = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _speechGate = new(1, 1);

    public void NotifyAccepted(OrderState order) => NotifyOnce(
        "accepted:" + order.OrderKey,
        $"桌台 {order.TableId}，买{SideText(order.Side)} {order.Amount:0.##}，下单成功",
        "/System/Library/Sounds/Glass.aiff");

    public void NotifySettlement(OrderState order, bool won) => NotifyOnce(
        "settled:" + order.OrderKey,
        $"桌台 {order.TableId}，本局投注{(won ? "获胜" : "落败")}",
        won ? "/System/Library/Sounds/Hero.aiff" : "/System/Library/Sounds/Basso.aiff");

    private void NotifyOnce(string notificationKey, string message, string fallbackSound)
    {
        lock (_lock)
        {
            if (!_notifiedOrders.Add(notificationKey)) return;
        }
        _ = Task.Run(async () =>
        {
            await _speechGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await RunAsync("/usr/bin/say", ["-v", "Tingting", message]).ConfigureAwait(false))
                    await RunAsync("/usr/bin/afplay", [fallbackSound]).ConfigureAwait(false);
            }
            finally
            {
                _speechGate.Release();
            }
        });
    }

    private static async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using Process? process = Process.Start(startInfo);
            if (process is null) return false;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SideText(BetSide side) => side == BetSide.Banker ? "庄" : "闲";
}
