using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
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
        SystemSounds.Asterisk);

    public void NotifySettlement(OrderState order, bool won) => NotifyOnce(
        "settled:" + order.OrderKey,
        $"桌台 {order.TableId}，本局投注{(won ? "获胜" : "落败")}",
        won ? SystemSounds.Exclamation : SystemSounds.Hand);

    private void NotifyOnce(string notificationKey, string message, SystemSound fallbackSound)
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
                if (!Speak(message)) fallbackSound.Play();
            }
            catch (InvalidOperationException) { }
            finally
            {
                _speechGate.Release();
            }
        });
    }

    private static bool Speak(string message)
    {
        object? voice = null;
        try
        {
            Type? voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null || Activator.CreateInstance(voiceType) is not { } instance) return false;
            voice = instance;
            voiceType.InvokeMember("Rate", BindingFlags.SetProperty, null, voice, [0]);
            voiceType.InvokeMember("Volume", BindingFlags.SetProperty, null, voice, [100]);
            voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [message, 0]);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice);
        }
    }

    private static string SideText(BetSide side) => side == BetSide.Banker ? "庄" : "闲";
}
