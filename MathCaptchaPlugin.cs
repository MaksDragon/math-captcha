using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MathCaptcha;

[ApiVersion(2, 1)]
public class MathCaptchaPlugin : TerrariaPlugin
{
    public override string Name => "MathCaptcha";
    public override string Author => "ChatGPT";
    public override string Description => "Math captcha protection with toggle and spam features";
    public override Version Version => new(1, 4, 1);

    private readonly Dictionary<int, CaptchaSession> _sessions = new();
    private readonly Random _random = new();
    
    private static bool _isEnabled = true;

    public MathCaptchaPlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        // Изменили хук ServerJoin на GreetPlayer (когда игрок уже вошел в мир)
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
        ServerApi.Hooks.ServerChat.Register(this, OnChat);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);

        Commands.ChatCommands.Add(new Command("captcha.admin", CaptchaCommand, "captcha")
        {
            HelpText = "Включить или отключить проверку капчи."
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreetPlayer);
            ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
        }

        base.Dispose(disposing);
    }

    private void CaptchaCommand(CommandArgs args)
    {
        _isEnabled = !_isEnabled;

        if (_isEnabled)
        {
            args.Player.SendSuccessMessage("[MathCaptcha] Проверка капчи включена для новых игроков.");
        }
        else
        {
            args.Player.SendWarningMessage("[MathCaptcha] Проверка капчи отключена.");
            _sessions.Clear();
        }
    }

    private void OnGreetPlayer(GreetPlayerEventArgs args)
    {
        if (!_isEnabled)
            return;

        var player = TShock.Players[args.Who];

        if (player == null || !player.Active)
            return;

        // Пропуск для админов
        if (player.HasPermission("captcha.bypass"))
            return;

        int a = _random.Next(1, 21);
        int b = _random.Next(1, 21);
        int answer = a + b;

        _sessions[player.Index] = new CaptchaSession
        {
            NumA = a,
            NumB = b,
            Answer = answer,
            ExpireTime = DateTime.UtcNow.AddSeconds(50),
            LastNotificationTime = DateTime.UtcNow
        };

        SendCaptchaPrompt(player, a, b);
    }

    private void OnChat(ServerChatEventArgs args)
    {
        var player = TShock.Players[args.Who];

        if (player == null)
            return;

        if (!_sessions.TryGetValue(player.Index, out var session))
            return;

        // Блокируем отправку сообщения в общий чат, пока капча не пройдена
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
            player.SendErrorMessage("Неверный ответ. Попробуйте еще раз!");
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
            var player = TShock.Players[playerIndex];

            // Если игрок сам вышел во время прохождения — просто удаляем сессию
            if (player == null || !player.Active)
            {
                _sessions.Remove(playerIndex);
                continue;
            }

            // Проверка на истечение 50 секунд
            if (now >= session.ExpireTime)
            {
                _sessions.Remove(playerIndex); // Удаляем сессию СРАЗУ перед действиями во избежание циклов

                try
                {
                    // Выполняем команду бана от имени сервера
                    TShockAPI.Commands.HandleCommand(
                        TSPlayer.Server,
                        $"/ban add {player.Name} \"Не прошел капчу\" 0d15m0s -ip"
                    );

                    TShock.Log.ConsoleInfo($"[MathCaptcha] Игрок {player.Name} забанен на 15 минут за провал капчи.");

                    player.Kick("Вы не прошли капчу. Бан на 15 минут.", true, true, "MathCaptcha");
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError($"[MathCaptcha] Ошибка при попытке забанить игрока {player.Name}: {ex}");
                }
                continue;
            }

            // Повторяющийся спам каждые 10 секунд
            if ((now - session.LastNotificationTime).TotalSeconds >= 10)
            {
                int timeLeft = (int)Math.Ceiling((session.ExpireTime - now).TotalSeconds);
                if (timeLeft > 0)
                {
                    SendCaptchaPrompt(player, session.NumA, session.NumB, timeLeft);
                }
                session.LastNotificationTime = now;
            }
        }
    }

    private static void SendCaptchaPrompt(TSPlayer player, int a, int b, int timeLeft = 50)
    {
        if (player == null || !player.Active) return;

        player.SendInfoMessage(" ");
        player.SendSuccessMessage("=== ВНИМАНИЕ: КАПЧА ===");
        player.SendWarningMessage($"Решите пример: {a} + {b}");
        player.SendWarningMessage("Введите ответ в чат, иначе вы будете забанены!");
        player.SendErrorMessage($"Осталось времени: {timeLeft} сек.");
        player.SendInfoMessage(" ");
    }

    private class CaptchaSession
    {
        public int NumA { get; set; }
        public int NumB { get; set; }
        public int Answer { get; set; }
        public DateTime ExpireTime { get; set; }
        public DateTime LastNotificationTime { get; set; }
    }
}
