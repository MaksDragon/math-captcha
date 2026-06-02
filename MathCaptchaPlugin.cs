using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MathCaptcha;

internal class CaptchaSession
{
    public int NumA { get; set; }
    public int NumB { get; set; }
    public int Answer { get; set; }
    public DateTime ExpireTime { get; set; }
    public DateTime LastNotificationTime { get; set; }
    
    // Переменные для защиты от флуда в чате
    public int MessageCountCurrentSecond { get; set; }
    public DateTime LastMessageTimestamp { get; set; } = DateTime.UtcNow;
    public int TotalMessagesSent { get; set; }

    // Фиксация координат для защиты от читов на движение
    public float SpawnX { get; set; } = 0;
    public float SpawnY { get; set; } = 0;
}

[ApiVersion(2, 1)]
public class MathCaptchaPlugin : TerrariaPlugin
{
    public override string Name => "MathCaptcha";
    public override string Author => "ChatGPT & Gemini";
    public override string Description => "Math captcha with absolute movement lock and exploit mitigation";
    public override Version Version => new(1, 6, 4);

    private readonly Dictionary<int, CaptchaSession> _sessions = new();
    private readonly Random _random = new();
    
    private static bool _isEnabled = true;

    public MathCaptchaPlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
        ServerApi.Hooks.ServerChat.Register(this, OnChat); 
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate); 
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, -2000000); 

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
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
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
            foreach (var index in _sessions.Keys)
            {
                var p = TShock.Players[index];
                if (p != null && p.Active) p.GodMode = false;
            }
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
            LastNotificationTime = DateTime.UtcNow,
            SpawnX = player.X, 
            SpawnY = player.Y
        };

        player.GodMode = true;

        SendCaptchaPrompt(player, a, b);
    }

    private void OnChat(ServerChatEventArgs args)
    {
        var player = TShock.Players[args.Who];
        if (player == null)
            return;

        if (!_sessions.TryGetValue(player.Index, out var session))
            return;

        // МГНОВЕННЫЙ БЛОК: Сообщение гарантированно не улетит в глобальный чат
        args.Handled = true;

        // --- ИСПРАВЛЕННЫЙ БЛОК КОНТРОЛЯ ФЛУДА ---
        var now = DateTime.UtcNow;
        if ((now - session.LastMessageTimestamp).TotalSeconds < 1.0)
        {
            session.MessageCountCurrentSecond++;
        }
        else
        {
            session.MessageCountCurrentSecond = 1;
            session.LastMessageTimestamp = now;
        }

        session.TotalMessagesSent++;

        // Если игрок отправил больше 5 сообщений ВСЕГО или пишет чаще 2 сообщений в секунду — ТИХИЙ КИК
        if (session.TotalMessagesSent > 5 || session.MessageCountCurrentSecond > 2)
        {
            _sessions.Remove(player.Index);
            player.Disconnect("MathCaptcha: Превышен лимит попыток ввода / флуд.");
            return;
        }
        // ----------------------------------------

        string text = args.Text.Trim();

        if (text.StartsWith("/") || text.StartsWith("."))
        {
            player.SendErrorMessage("Использование команд заблокировано! Сначала решите капчу.");
            return;
        }

        if (!int.TryParse(text, out int result))
        {
            player.SendErrorMessage($"Введите число. У вас осталось {5 - session.TotalMessagesSent} попыток.");
            return;
        }

        if (result == session.Answer)
        {
            _sessions.Remove(player.Index);
            player.GodMode = false;
            
            player.SetBuff(44, 0);  
            player.SetBuff(149, 0); 
            
            player.SendSuccessMessage("Капча успешно пройдена! Приятной игры.");
        }
        else
        {
            player.SendErrorMessage($"Неверный ответ. У вас осталось {5 - session.TotalMessagesSent} попыток.");
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

            if (player == null || !player.Active)
            {
                _sessions.Remove(playerIndex);
                continue;
            }

            player.SetBuff(44, 60, true);  
            player.SetBuff(149, 60, true); 
            player.GodMode = true;

            if (session.SpawnX != 0 && session.SpawnY != 0)
            {
                if (Math.Abs(player.X - session.SpawnX) > 32 || Math.Abs(player.Y - session.SpawnY) > 32)
                {
                    player.Teleport(session.SpawnX, session.SpawnY);
                }
            }

            if (now >= session.ExpireTime)
            {
                _sessions.Remove(playerIndex);

                try
                {
                    TShockAPI.Commands.HandleCommand(
                        TSPlayer.Server,
                        $"/ban add {player.Name} \"Не прошел капчу\" 0d15m0s -ip"
                    );

                    TShock.Log.ConsoleInfo($"[MathCaptcha] Игрок {player.Name} забанен на 15 минут за провал капчи.");
                    player.Kick("Вы не прошли капчу. Бан на 15 минут.", true, true, "MathCaptcha");
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError($"[MathCaptcha] Ошибка при бане игрока {player.Name}: {ex}");
                }
                continue;
            }

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

    private void OnGetData(GetDataEventArgs args)
    {
        if (!_isEnabled) return;

        if (_sessions.TryGetValue(args.Msg.whoAmI, out _))
        {
            if (args.MsgID == PacketTypes.LoadNetModule)
            {
                // Оставляем ТУТ исключительно защиту от огромных краш-пакетов (длина пакета чата)
                if (args.Length > 60)
                {
                    args.Handled = true;
                    var attacker = TShock.Players[args.Msg.whoAmI];
                    if (attacker != null && attacker.Active)
                    {
                        attacker.Disconnect("MathCaptcha: Превышен размер пакета чата.");
                    }
                    return;
                }
            }

            // Блокировка всех физических действий на сервере
            switch (args.MsgID)
            {
                case PacketTypes.Tile:             
                case PacketTypes.ItemDrop:        
                case PacketTypes.ChestOpen:       
                case PacketTypes.ProjectileNew:   
                case PacketTypes.PlayerSlot:      
                case PacketTypes.TogglePvp:        
                case PacketTypes.LiquidSet:       
                    args.Handled = true;          
                    break;
            }
        }
    }

    private static void SendCaptchaPrompt(TSPlayer player, int a, int b, int timeLeft = 50)
    {
        if (player == null || !player.Active) return;

        player.SendInfoMessage(" ");
        player.SendSuccessMessage("=== ВНИМАНИЕ: КАПЧА ===");
        player.SendWarningMessage($"Решите пример: {a} + {b}");
        player.SendWarningMessage("Вы полностью обездвижены сервером. Введите ответ в чат!");
        player.SendErrorMessage($"Осталось времени: {timeLeft} сек. У вас есть всего 5 попыток!");
        player.SendInfoMessage(" ");
    }
}
