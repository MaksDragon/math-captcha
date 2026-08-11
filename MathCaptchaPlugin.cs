using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace MathCaptcha;

internal sealed class CaptchaConfig
{
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 50;
    public int ReminderSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    public int MaxMessagesPerWindow { get; set; } = 2;
    public double FloodWindowSeconds { get; set; } = 1.0;
    public float MovementTolerancePixels { get; set; } = 96f;
    public bool BlockPlayerUpdate { get; set; } = true;
    public int BuffRefreshSeconds { get; set; } = 1;
    public int BuffDurationTicks { get; set; } = 180;
    public int MaxChatPacketLength { get; set; } = 60;
    public bool BanOnTimeout { get; set; } = true;
    public string BanDuration { get; set; } = "15m";
    public string BanReason { get; set; } = "Не прошёл капчу";
    public int TempBanMinutes { get; set; } = 15;
    public bool LogEvents { get; set; } = true;
}

internal sealed class CaptchaSession
{
    public int NumA { get; init; }
    public int NumB { get; init; }
    public int Answer { get; init; }
    public DateTime ExpireTime { get; init; }
    public DateTime LastNotificationTime { get; set; }
    public DateTime NextBuffRefresh { get; set; }
    public int AttemptsUsed { get; set; }
    public ConcurrentQueue<DateTime> MessageTimes { get; } = new();
    public bool HasSpawnLock { get; set; }
    public float SpawnX { get; set; }
    public float SpawnY { get; set; }
    public string PlayerName { get; init; } = string.Empty;
    public string PlayerIP { get; init; } = "unknown";
}

[ApiVersion(2, 1)]
public class MathCaptchaPlugin : TerrariaPlugin
{
    private const int SilenceBuffId = 44;
    private const int WebbedBuffId = 149;

    public override string Name => "MathCaptcha";
    public override string Author => "ChatGPT & Gemini";
    public override string Description => "Math captcha with movement lock, config, flood protection, temp bans and timeout ban.";
    public override Version Version => new(1, 8, 1);

    private readonly ConcurrentDictionary<int, CaptchaSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _preCaptchaDisconnects = new();
    private readonly ConcurrentDictionary<string, DateTime> _tempIpBans = new();
    private readonly ConcurrentDictionary<string, DateTime> _tempUuidBans = new();
    private readonly object _configLock = new();

    private CaptchaConfig _config = new();
    private bool _isEnabled = true;

    public MathCaptchaPlugin(Main game) : base(game)
    {
    }

    private string ConfigPath => Path.Combine(AppContext.BaseDirectory, "MathCaptcha.json");

    public override void Initialize()
    {
        LoadConfig();
        _isEnabled = _config.Enabled;

        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreetPlayer);
        ServerApi.Hooks.ServerChat.Register(this, OnChat);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, -2000000);

        Commands.ChatCommands.Add(new Command("captcha.admin", CaptchaCommand, "captcha")
        {
            HelpText = "Управление MathCaptcha: /captcha [enable|disable|reload|status]."
        });

        LogInfo($"Плагин загружен. Версия: {Version}. Конфиг: {ConfigPath}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearAllSessions(unlockPlayers: true);

            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreetPlayer);
            ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
        }

        base.Dispose(disposing);
    }

    #region Config

    private void LoadConfig()
    {
        lock (_configLock)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<CaptchaConfig>(json);
                    _config = cfg ?? new CaptchaConfig();
                    SaveConfigInternal();
                }
                else
                {
                    SaveConfigInternal();
                }
            }
            catch (Exception ex)
            {
                _config = new CaptchaConfig();
                try
                {
                    TShock.Log.ConsoleError($"[MathCaptcha] Ошибка загрузки конфига: {ex.Message}");
                }
                catch { }
            }
        }
    }

    private void SaveConfig()
    {
        lock (_configLock)
        {
            SaveConfigInternal();
        }
    }

    private void SaveConfigInternal()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_config, options);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            try
            {
                TShock.Log.ConsoleError($"[MathCaptcha] Ошибка сохранения конфига: {ex.Message}");
            }
            catch { }
        }
    }

    #endregion

    #region Command

    private void CaptchaCommand(CommandArgs args)
    {
        string sub = args.Parameters is { Count: > 0 }
            ? args.Parameters[0] ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(sub))
        {
            SetEnabled(!_isEnabled, args.Player);
            return;
        }

        switch (sub.ToLowerInvariant())
        {
            case "enable":
                SetEnabled(true, args.Player);
                break;

            case "disable":
                SetEnabled(false, args.Player);
                break;

            case "reload":
                LoadConfig();
                SetEnabled(_config.Enabled, args.Player, saveConfig: false);
                args.Player?.SendSuccessMessage("[MathCaptcha] Конфиг перезагружен.");
                break;

            case "status":
                args.Player?.SendInfoMessage(
                    $"[MathCaptcha] Состояние: {(_isEnabled ? "включена" : "выключена")}. " +
                    $"Активных капч: {_sessions.Count}. " +
                    $"Временных банов IP: {_tempIpBans.Count}, UUID: {_tempUuidBans.Count}."
                );
                break;

            default:
                args.Player?.SendInfoMessage("Использование: /captcha [enable|disable|reload|status]");
                break;
        }
    }

    private void SetEnabled(bool value, TSPlayer player, bool saveConfig = true)
    {
        _isEnabled = value;
        _config.Enabled = value;

        if (saveConfig)
            SaveConfig();

        if (!value)
            ClearAllSessions(unlockPlayers: true);

        if (player == null)
            return;

        if (value)
            player.SendSuccessMessage("[MathCaptcha] Проверка капчи включена для новых игроков.");
        else
            player.SendWarningMessage("[MathCaptcha] Проверка капчи отключена. Активные игроки разблокированы.");
    }

    #endregion

    #region Hooks

    private void OnGreetPlayer(GreetPlayerEventArgs args)
    {
        if (!_isEnabled)
            return;

        var player = GetPlayer(args.Who);
        if (player == null || !player.Active)
            return;

        if (IsTempBanned(player, out string banMessage))
        {
            LogInfo($"Игрок {player.Name} (IP={player.IP}) временно забанен. Отключаем при входе.");
            player.Disconnect(banMessage);
            return;
        }

        if (player.HasPermission("captcha.bypass"))
            return;

        int a = Random.Shared.Next(1, 21);
        int b = Random.Shared.Next(1, 21);

        var session = new CaptchaSession
        {
            NumA = a,
            NumB = b,
            Answer = a + b,
            ExpireTime = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.TimeoutSeconds)),
            LastNotificationTime = DateTime.UtcNow,
            NextBuffRefresh = DateTime.UtcNow,
            SpawnX = player.X,
            SpawnY = player.Y,
            HasSpawnLock = player.X != 0f || player.Y != 0f,
            PlayerName = player.Name ?? $"player{player.Index}",
            PlayerIP = player.IP ?? "unknown"
        };

        _sessions[player.Index] = session;

        player.GodMode = true;
        ApplyLockBuffs(player);
        SendCaptchaPrompt(player, a, b, Math.Max(5, _config.TimeoutSeconds), Math.Max(1, _config.MaxAttempts));

        LogInfo($"Игрок {session.PlayerName} начал капчу. IP={session.PlayerIP}, UUID={player.UUID}");
    }

    private void OnChat(ServerChatEventArgs args)
    {
        var player = GetPlayer(args.Who);
        if (player == null)
            return;

        if (!_sessions.TryGetValue(player.Index, out var session))
            return;

        args.Handled = true;

        var now = DateTime.UtcNow;

        if (now >= session.ExpireTime)
        {
            if (_sessions.TryRemove(player.Index, out _))
            {
                LogInfo($"Игрок {session.PlayerName} не прошёл капчу: таймаут при попытке ответа.");
                AddTempBan(player, now.AddMinutes(Math.Max(1, _config.TempBanMinutes)));
                BanAndKick(player, "timeout");
            }
            return;
        }

        double floodWindow = Math.Max(0.1, _config.FloodWindowSeconds);
        int maxMessages = Math.Max(1, _config.MaxMessagesPerWindow);

        while (session.MessageTimes.TryPeek(out var oldTime) &&
               (now - oldTime).TotalSeconds >= floodWindow)
        {
            session.MessageTimes.TryDequeue(out _);
        }

        if (session.MessageTimes.Count >= maxMessages)
        {
            if (_sessions.TryRemove(player.Index, out _))
            {
                LogInfo($"Игрок {session.PlayerName} отключён за флуд на капче.");
                player.Disconnect("MathCaptcha: Превышен лимит сообщений.");
            }
            return;
        }

        session.MessageTimes.Enqueue(now);

        string text = (args.Text ?? string.Empty).Trim();

        if (text.StartsWith('/') || text.StartsWith('.'))
        {
            player.SendErrorMessage("Использование команд заблокировано! Сначала решите капчу.");
            return;
        }

        int maxAttempts = Math.Max(1, _config.MaxAttempts);

        if (session.AttemptsUsed >= maxAttempts)
        {
            if (_sessions.TryRemove(player.Index, out _))
            {
                LogInfo($"Игрок {session.PlayerName} исчерпал лимит попыток капчи.");
                player.Disconnect("MathCaptcha: Превышен лимит попыток.");
            }
            return;
        }

        if (!int.TryParse(text, out int result))
        {
            session.AttemptsUsed++;
            int left = Math.Max(0, maxAttempts - session.AttemptsUsed);

            if (left <= 0)
            {
                if (_sessions.TryRemove(player.Index, out _))
                {
                    LogInfo($"Игрок {session.PlayerName} ввёл нечисловой ответ и исчерпал попытки.");
                    player.Disconnect("MathCaptcha: Введите число. Попытки закончились.");
                }
            }
            else
            {
                player.SendErrorMessage($"Введите число. Осталось попыток: {left}.");
            }
            return;
        }

        if (result == session.Answer)
        {
            if (_sessions.TryRemove(player.Index, out _))
            {
                UnlockPlayer(player);
                player.SendSuccessMessage("Капча успешно пройдена! Приятной игры.");
                LogInfo($"Игрок {session.PlayerName} успешно прошёл капчу.");
            }
            return;
        }

        session.AttemptsUsed++;
        int attemptsLeft = Math.Max(0, maxAttempts - session.AttemptsUsed);

        if (attemptsLeft <= 0)
        {
            if (_sessions.TryRemove(player.Index, out _))
            {
                LogInfo($"Игрок {session.PlayerName} дал неверный ответ и исчерпал попытки.");
                player.Disconnect("MathCaptcha: Неверный ответ. Попытки закончились.");
            }
        }
        else
        {
            player.SendErrorMessage($"Неверный ответ. Осталось попыток: {attemptsLeft}.");
        }
    }

    private void OnUpdate(EventArgs args)
    {
        CleanupTempBans();

        if (_sessions.IsEmpty)
            return;

        var now = DateTime.UtcNow;

        foreach (var entry in _sessions.ToArray())
        {
            int index = entry.Key;
            var session = entry.Value;
            var player = GetPlayer(index);

            if (player == null || !player.Active)
            {
                if (_sessions.TryRemove(index, out var removed))
                {
                    string name = removed?.PlayerName ?? $"index:{index}";
                    string ip = removed?.PlayerIP ?? "unknown";

                    int count = _preCaptchaDisconnects.AddOrUpdate(ip, 1, (_, oldValue) => oldValue + 1);

                    LogInfo($"Игрок {name} отключился до прохождения капчи. Бан за выход не выдаётся. Отключений с IP {ip}: {count}.");

                    if (count >= 5)
                        LogWarning($"С IP {ip} уже {count} отключений до капчи. Возможно, это бот или сканер.");
                }
                continue;
            }

            if (!session.HasSpawnLock)
            {
                if (player.X != 0f || player.Y != 0f)
                {
                    session.SpawnX = player.X;
                    session.SpawnY = player.Y;
                    session.HasSpawnLock = true;
                }
            }
            else
            {
                float tolerance = Math.Max(16f, _config.MovementTolerancePixels);

                if (Math.Abs(player.X - session.SpawnX) > tolerance ||
                    Math.Abs(player.Y - session.SpawnY) > tolerance)
                {
                    player.Teleport(session.SpawnX, session.SpawnY);
                }
            }

            if (now >= session.NextBuffRefresh)
            {
                ApplyLockBuffs(player);
                session.NextBuffRefresh = now.AddSeconds(Math.Max(1, _config.BuffRefreshSeconds));
            }

            if (now >= session.ExpireTime)
            {
                if (_sessions.TryRemove(index, out _))
                {
                    LogInfo($"Игрок {session.PlayerName} не прошёл капчу: таймаут.");
                    AddTempBan(player, now.AddMinutes(Math.Max(1, _config.TempBanMinutes)));
                    BanAndKick(player, "timeout");
                }
                continue;
            }

            if ((now - session.LastNotificationTime).TotalSeconds >= Math.Max(1, _config.ReminderSeconds))
            {
                int timeLeft = (int)Math.Ceiling((session.ExpireTime - now).TotalSeconds);
                if (timeLeft > 0)
                    SendCaptchaPrompt(player, session.NumA, session.NumB, timeLeft, Math.Max(1, _config.MaxAttempts));

                session.LastNotificationTime = now;
            }
        }
    }

    private void OnGetData(GetDataEventArgs args)
    {
        if (!_isEnabled)
            return;

        var msg = args.Msg;
        if (msg == null)
            return;

        int who = msg.whoAmI;

        if (!_sessions.ContainsKey(who))
            return;

        if (args.MsgID == PacketTypes.LoadNetModule)
        {
            int maxLength = Math.Max(20, _config.MaxChatPacketLength);

            if (args.Length > maxLength)
            {
                args.Handled = true;

                var attacker = GetPlayer(who);
                if (attacker is { Active: true })
                {
                    LogInfo($"Игрок {attacker.Name} отправил слишком большой пакет чата во время капчи.");
                    attacker.Disconnect("MathCaptcha: Превышен размер пакета чата.");
                }
            }
            return;
        }

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

        if (_config.BlockPlayerUpdate && args.MsgID == PacketTypes.PlayerUpdate)
        {
            args.Handled = true;
        }
    }

    #endregion

    #region Lock / unlock

    private void ApplyLockBuffs(TSPlayer player)
    {
        if (player == null || !player.Active)
            return;

        player.GodMode = true;

        int duration = Math.Max(60, _config.BuffDurationTicks);

        player.SetBuff(SilenceBuffId, duration, true);
        player.SetBuff(WebbedBuffId, duration, true);
    }

    private void UnlockPlayer(TSPlayer player)
    {
        if (player == null || !player.Active)
            return;

        player.GodMode = false;
        player.SetBuff(SilenceBuffId, 0);
        player.SetBuff(WebbedBuffId, 0);
    }

    private void ClearAllSessions(bool unlockPlayers)
    {
        try
        {
            if (unlockPlayers)
            {
                foreach (var entry in _sessions.ToArray())
                {
                    var player = GetPlayer(entry.Key);
                    UnlockPlayer(player);
                }
            }
        }
        catch { }

        _sessions.Clear();
    }

    #endregion

    #region Ban / kick

    private void BanAndKick(TSPlayer player, string internalReason)
    {
        if (player == null)
            return;

        string duration = SafeBanDuration(_config.BanDuration);

        try
        {
            if (_config.BanOnTimeout)
            {
                TryTShockBan(player);
            }

            string kickMessage = _config.BanOnTimeout
                ? $"Вы не прошли капчу. Бан: {duration}."
                : "Вы не прошли капчу.";

            player.Kick(kickMessage, true, true, "MathCaptcha");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка при бане/кике игрока {player?.Name}: {ex}");

            try
            {
                player?.Disconnect("MathCaptcha: проверка не пройдена.");
            }
            catch { }
        }
    }

    #endregion

    #region Временные баны плагина

    private void AddTempBan(TSPlayer player, DateTime until)
    {
        if (player == null)
            return;

        if (!string.IsNullOrWhiteSpace(player.IP))
        {
            _tempIpBans[player.IP] = until;
            LogInfo($"Временный бан добавлен по IP: {player.IP} до {until:u}");
        }

        if (!string.IsNullOrWhiteSpace(player.UUID))
        {
            _tempUuidBans[player.UUID] = until;
            LogInfo($"Временный бан добавлен по UUID: {player.UUID} до {until:u}");
        }
    }

    private bool IsTempBanned(TSPlayer player, out string message)
    {
        if (player == null)
        {
            message = string.Empty;
            return false;
        }

        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(player.IP) && _tempIpBans.TryGetValue(player.IP, out DateTime ipUntil))
        {
            if (now < ipUntil)
            {
                int minutesLeft = (int)Math.Ceiling((ipUntil - now).TotalMinutes);
                message = $"MathCaptcha: временный бан за провал капчи. Осталось минут: {minutesLeft}.";
                return true;
            }

            _tempIpBans.TryRemove(player.IP, out _);
        }

        if (!string.IsNullOrWhiteSpace(player.UUID) && _tempUuidBans.TryGetValue(player.UUID, out DateTime uuidUntil))
        {
            if (now < uuidUntil)
            {
                int minutesLeft = (int)Math.Ceiling((uuidUntil - now).TotalMinutes);
                message = $"MathCaptcha: временный бан за провал капчи. Осталось минут: {minutesLeft}.";
                return true;
            }

            _tempUuidBans.TryRemove(player.UUID, out _);
        }

        message = string.Empty;
        return false;
    }

    private void CleanupTempBans()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in _tempIpBans.ToArray())
        {
            if (entry.Value <= now)
                _tempIpBans.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _tempUuidBans.ToArray())
        {
            if (entry.Value <= now)
                _tempUuidBans.TryRemove(entry.Key, out _);
        }
    }

    private void TryTShockBan(TSPlayer player)
    {
        if (player == null)
            return;

        try
        {
            string reason = string.IsNullOrWhiteSpace(_config.BanReason) ? "Не прошёл капчу" : _config.BanReason;
            string duration = SafeBanDuration(_config.BanDuration);

            // Бан по IP
            if (!string.IsNullOrWhiteSpace(player.IP))
            {
                bool ipBan = Commands.HandleCommand(
                    TSPlayer.Server,
                    $"/ban add ip:{player.IP} \"{reason}\" {duration}"
                );

                LogInfo($"Попытка бана по IP {player.IP}: {(ipBan ? "успешно" : "не удалось")}");
            }

            // Бан по UUID
            if (!string.IsNullOrWhiteSpace(player.UUID))
            {
                bool uuidBan = Commands.HandleCommand(
                    TSPlayer.Server,
                    $"/ban add uuid:{player.UUID} \"{reason}\" {duration}"
                );

                LogInfo($"Попытка бана по UUID {player.UUID}: {(uuidBan ? "успешно" : "не удалось")}");
            }

            // Бан по имени
            if (!string.IsNullOrWhiteSpace(player.Name))
            {
                string safeName = player.Name.Replace("\"", string.Empty);

                bool nameBan = Commands.HandleCommand(
                    TSPlayer.Server,
                    $"/ban add {safeName} \"{reason}\" {duration}"
                );

                LogInfo($"Попытка бана по имени {safeName}: {(nameBan ? "успешно" : "не удалось")}");
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка при попытке TShock-бана: {ex}");
        }
    }

    #endregion

    #region Helpers

    private static TSPlayer GetPlayer(int index)
    {
        var players = TShock.Players;
        if (players == null || index < 0 || index >= players.Length)
            return null;

        return players[index];
    }

    private static string SafeBanDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return "15m";

        var cleaned = new string(duration.Where(char.IsLetterOrDigit).ToArray());

        return cleaned.Length == 0 ? "15m" : cleaned;
    }

    private void LogInfo(string message)
    {
        if (!_config.LogEvents)
            return;

        try
        {
            TShock.Log.ConsoleInfo($"[MathCaptcha] {message}");
        }
        catch { }
    }

    private void LogWarning(string message)
    {
        if (!_config.LogEvents)
            return;

        try
        {
            TShock.Log.ConsoleInfo($"[MathCaptcha] WARNING: {message}");
        }
        catch { }
    }

    private void LogError(string message)
    {
        try
        {
            TShock.Log.ConsoleError($"[MathCaptcha] {message}");
        }
        catch { }
    }

    private static void SendCaptchaPrompt(TSPlayer player, int a, int b, int timeLeft, int maxAttempts)
    {
        if (player == null || !player.Active)
            return;

        player.SendInfoMessage(" ");
        player.SendSuccessMessage("=== ВНИМАНИЕ: КАПЧА ===");
        player.SendWarningMessage($"Решите пример: {a} + {b}");
        player.SendWarningMessage("Вы полностью обездвижены сервером. Введите ответ в чат!");
        player.SendErrorMessage($"Осталось времени: {timeLeft} сек. У вас есть {maxAttempts} попыток!");
        player.SendInfoMessage(" ");
    }

    #endregion
}