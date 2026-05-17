using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace MathCaptcha;

[ApiVersion(2, 1)]
public class MathCaptchaPlugin : TerrariaPlugin
{
    public override string Name => "MathCaptcha";
    public override string Author => "ChatGPT";
    public override string Description => "Math captcha protection";
    public override Version Version => new(1, 0, 0);

    private readonly Dictionary<int, CaptchaSession> _sessions = new();

    private readonly Random _random = new();

    public MathCaptchaPlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        ServerApi.Hooks.ServerJoin.Register(this, OnJoin);
        ServerApi.Hooks.ServerChat.Register(this, OnChat);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.ServerJoin.Deregister(this, OnJoin);
            ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
        }

        base.Dispose(disposing);
    }

    private void OnJoin(JoinEventArgs args)
    {
        var player = TShock.Players[args.Who];

        if (player == null)
            return;

        // Пропуск для админов
        if (player.HasPermission("captcha.bypass"))
            return;

        int a = _random.Next(1, 21);
        int b = _random.Next(1, 21);

        int answer = a + b;

        _sessions[player.Index] = new CaptchaSession
        {
            Answer = answer,
            ExpireTime = DateTime.UtcNow.AddSeconds(15)
        };

        player.SendInfoMessage(" ");
        player.SendSuccessMessage("=== CAPTCHA ===");
        player.SendWarningMessage($"Решите пример: {a} + {b}");
        player.SendWarningMessage("Введите ответ в чат.");
        player.SendErrorMessage("У вас 15 секунд.");
        player.SendInfoMessage(" ");
    }

    private void OnChat(ServerChatEventArgs args)
    {
        var player = TShock.Players[args.Who];

        if (player == null)
            return;

        if (!_sessions.TryGetValue(player.Index, out var session))
            return;

        args.Handled = true;

        string text = args.Text.Trim();

        if (!int.TryParse(text, out int result))
        {
            player.SendErrorMessage("Введите число.");
            return;
        }

        if (result == session.Answer)
        {
            _sessions.Remove(player.Index);

            player.SendSuccessMessage("Капча успешно пройдена!");
        }
        else
        {
            player.SendErrorMessage("Неверный ответ.");
        }
    }

    private void OnUpdate(EventArgs args)
    {
        if (_sessions.Count == 0)
            return;

        var now = DateTime.UtcNow;

        foreach (var entry in _sessions.ToList())
        {
            int playerIndex = entry.Key;
            var session = entry.Value;

            if (now < session.ExpireTime)
                continue;

            var player = TShock.Players[playerIndex];

            if (player != null && player.Active)
            {
                try
                {
                    Commands.HandleCommand(
                        TSPlayer.Server,
                        $"/ban add {player.Name} \"Не прошел капчу\" 0d15m0s -ip"
                    );

                    TShock.Log.ConsoleInfo(
                        $"[MathCaptcha] Игрок {player.Name} забанен на 15 минут."
                    );

                    player.Kick(
                        "Вы не прошли капчу. Бан на 15 минут.",
                        true,
                        true,
                        "MathCaptcha"
                    );
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError(
                        $"[MathCaptcha] Ошибка бана: {ex}"
                    );
                }
            }

            _sessions.Remove(playerIndex);
        }
    }

    private class CaptchaSession
    {
        public int Answer { get; set; }

        public DateTime ExpireTime { get; set; }
    }
}
