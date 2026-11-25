// Nextendo MCP server — HTTP JSON-RPC 2.0 at http://127.0.0.1:<port>/mcp/
// Default port 52773; if busy, tries the next ports automatically.
// Tools: game_state, emulator_state, screenshot, list_games, load_game,
//        key_press, joystick_set, touch_tap, mouse_move, rumble.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using Ryujinx.Ava.Systems;
using Ryujinx.Ava.UI.ViewModels;
using SkiaSharp;

namespace Ryujinx.Ava.Common
{
    public static class McpServer
    {
        private const int DefaultPort = 52773;
        private const int MaxPortAttempts = 32;
        private const int PanelWidth = 1280;
        private const int PanelHeight = 720;

        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static MainWindowViewModel _vm;
        private static int _boundPort = DefaultPort;

        // Virtual input state.
        private static ControllerKeys _heldButtons;
        private static long _buttonsUntilMs;
        private static readonly object _inputLock = new();

        public static bool IsRunning => _listener?.IsListening == true;

        /// <summary>Port actually bound after Start (may be higher than the default if that one was busy).</summary>
        public static int BoundPort => _boundPort;

        public static string EndpointUrl => $"http://127.0.0.1:{_boundPort}/mcp/";

        public static void Start(MainWindowViewModel vm)
        {
            _vm = vm;

            Exception lastError = null;

            for (int attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                int port = DefaultPort + attempt;
                HttpListener listener = null;

                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
                    listener.Start();

                    _listener = listener;
                    _boundPort = port;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => AcceptLoop(_cts.Token));

                    if (attempt > 0)
                    {
                        Logger.Warning?.Print(LogClass.Application,
                            $"[MCP] Port {DefaultPort} was busy — listening on {EndpointUrl} instead");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Application, $"[MCP] Server listening on {EndpointUrl}");
                    }

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    try
                    {
                        listener?.Stop();
                        listener?.Close();
                    }
                    catch { /* ignore cleanup failures */ }

                    Logger.Warning?.Print(LogClass.Application,
                        $"[MCP] Port {port} unavailable ({ex.Message}), trying {port + 1}…");
                }
            }

            Logger.Error?.Print(LogClass.Application,
                $"[MCP] Failed to start after {MaxPortAttempts} ports ({DefaultPort}–{DefaultPort + MaxPortAttempts - 1}): {lastError?.Message}");
        }

        public static void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
        }

        // Called once per emulator frame from AppHost.UpdateFrame so held input stays active.
        public static void FrameTick()
        {
            var device = _vm?.AppHost?.Device;
            if (device == null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ControllerKeys held;
            lock (_inputLock)
            {
                held = now < _buttonsUntilMs ? _heldButtons : 0;
            }

            if (held != 0)
            {
                device.Hid.Npads.Update(new[]
                {
                    new GamepadInput { PlayerId = PlayerIndex.Player1, Buttons = held }
                });
            }
        }

        private static async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch
                {
                    break; // listener stopped
                }
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.HttpMethod != "POST")
                {
                    WriteJson(ctx, 405, RpcError("method_not_allowed", "POST required"));
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                object result;
                object id = null;
                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    JsonElement root = doc.RootElement;
                    id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number
                        ? (object)idEl.GetInt32()
                        : null;

                    string method = root.TryGetProperty("method", out JsonElement m) ? m.GetString() : null;
                    result = method switch
                    {
                        "tools/list" => new { tools = Tools() },
                        "tools/call" => CallTool(root),
                        _ => RpcError("method_not_found", $"Unknown method '{method}'")
                    };
                }

                object resp = new { jsonrpc = "2.0", id, result };
                WriteJson(ctx, 200, resp);
            }
            catch (Exception ex)
            {
                WriteJson(ctx, 200, new { jsonrpc = "2.0", id = (object)null, error = new { code = -32700, message = ex.Message } });
            }
        }

        private static object CallTool(JsonElement root)
        {
            if (!root.TryGetProperty("params", out JsonElement p) ||
                !p.TryGetProperty("name", out JsonElement n))
            {
                return RpcError("invalid_params", "params.name required");
            }

            string name = n.GetString();
            JsonElement args = p.TryGetProperty("arguments", out JsonElement a) ? a : default;

            try
            {
                return name switch
                {
                    "game_state" => GameState(),
                    "emulator_state" => EmulatorState(),
                    "screenshot" => Screenshot(),
                    "list_games" => ListGames(),
                    "load_game" => LoadGame(args),
                    "key_press" => KeyPress(args),
                    "joystick_set" => JoystickSet(args),
                    "touch_tap" => TouchTap(args),
                    "mouse_move" => MouseMove(args),
                    "rumble" => new { ok = true, message = "rumble not supported" },
                    _ => RpcError("tool_not_found", $"Unknown tool '{name}'")
                };
            }
            catch (Exception ex)
            {
                return RpcError("tool_error", ex.Message);
            }
        }

        private static object[] Tools()
        {
            return new[]
            {
                Tool("game_state", "Get current game name, FPS, and resolution", new
                {
                    type = "object",
                    properties = new { }
                }),
                Tool("emulator_state", "Get whether a game is running and the loaded game path", new
                {
                    type = "object",
                    properties = new { }
                }),
                Tool("screenshot", "Capture the current game screen to PNG base64", new
                {
                    type = "object",
                    properties = new { }
                }),
                Tool("list_games", "List installed games (name, title id, path)", new
                {
                    type = "object",
                    properties = new { }
                }),
                Tool("load_game", "Launch an installed game by title id (hex) or path", new
                {
                    type = "object",
                    properties = new { game = new { type = "string" } },
                    required = new[] { "game" }
                }),
                Tool("key_press", "Press or hold a Switch input button", new
                {
                    type = "object",
                    properties = new
                    {
                        button = new { type = "string", description = "A|B|X|Y|L|R|ZL|ZR|Plus|Minus|DpadUp|DpadDown|DpadLeft|DpadRight|LStick|RStick" },
                        durationMs = new { type = "integer" }
                    },
                    required = new[] { "button" }
                }),
                Tool("joystick_set", "Set left joystick direction and hold duration", new
                {
                    type = "object",
                    properties = new
                    {
                        dx = new { type = "number" },
                        dy = new { type = "number" },
                        durationMs = new { type = "integer" }
                    }
                }),
                Tool("touch_tap", "Tap the touchscreen at normalized coordinates (0..1)", new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number" },
                        y = new { type = "number" }
                    }
                }),
                Tool("mouse_move", "Move the mouse to a given screen position (0..1 normalized)", new
                {
                    type = "object",
                    properties = new
                    {
                        x = new { type = "number" },
                        y = new { type = "number" }
                    }
                }),
                Tool("rumble", "Trigger controller rumble", new
                {
                    type = "object",
                    properties = new { }
                }),
            };
        }

        private static object Tool(string name, string desc, object schema)
        {
            return new { name, description = desc, inputSchema = schema };
        }

        private static object GameState()
        {
            var appHost = _vm?.AppHost;
            if (appHost?.Device?.Processes?.ActiveApplication == null)
            {
                return new { running = false };
            }

            string name = appHost.Device.Processes.ActiveApplication.Name;
            double fps = appHost.Device.Statistics.GetGameFrameRate();
            double frameTime = appHost.Device.Statistics.GetGameFrameTime();

            return new
            {
                running = true,
                name,
                fps = Math.Round(fps, 2),
                frameTime = Math.Round(frameTime, 2),
                width = appHost.Width,
                height = appHost.Height
            };
        }

        private static object EmulatorState()
        {
            var appHost = _vm?.AppHost;
            return new
            {
                running = appHost?.Device?.Processes?.ActiveApplication != null,
                appHostRunning = appHost != null,
                gamePath = appHost?.ApplicationPath,
                titleId = appHost?.ApplicationId is ulong tid ? $"0x{tid:X16}" : null
            };
        }

        private static object ListGames()
        {
            var apps = _vm?.Applications;
            if (apps == null) return new { games = Array.Empty<object>() };

            var games = apps
                .Where(a => a.Path != null)
                .Select(a => new
                {
                    name = a.Name,
                    titleId = $"0x{a.Id:X16}",
                    path = a.Path,
                    version = a.Version
                })
                .ToArray();

            return new { games };
        }

        private static object LoadGame(JsonElement args)
        {
            if (_vm?.AppHost != null)
            {
                return RpcError("game_running", "A game is already running. Close it before loading another.");
            }

            string target = args.TryGetProperty("game", out JsonElement g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(target))
            {
                return RpcError("invalid_params", "game (title id hex or path) is required");
            }

            var app = _vm?.Applications.FirstOrDefault(a =>
                a.Path == target ||
                a.Id.ToString("X16").Equals(target.Trim(), StringComparison.OrdinalIgnoreCase) ||
                $"0x{a.Id:X16}".Equals(target.Trim(), StringComparison.OrdinalIgnoreCase));

            if (app == null)
            {
                return RpcError("not_found", $"No installed game matches '{target}'");
            }

            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { await _vm.LoadApplication(app); }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"[MCP] LoadGame failed: {ex.Message}");
                }
            });

            return new { ok = true, launching = app.Name };
        }

        private static object KeyPress(JsonElement args)
        {
            string button = args.TryGetProperty("button", out JsonElement b) ? b.GetString() : "A";
            int duration = args.TryGetProperty("durationMs", out JsonElement d) ? d.GetInt32() : 120;

            ControllerKeys key = button.ToLowerInvariant() switch
            {
                "a" => ControllerKeys.A,
                "b" => ControllerKeys.B,
                "x" => ControllerKeys.X,
                "y" => ControllerKeys.Y,
                "l" => ControllerKeys.L,
                "r" => ControllerKeys.R,
                "zl" => ControllerKeys.Zl,
                "zr" => ControllerKeys.Zr,
                "plus" => ControllerKeys.Plus,
                "minus" => ControllerKeys.Minus,
                "dpadup" => ControllerKeys.DpadUp,
                "dpaddown" => ControllerKeys.DpadDown,
                "dpadleft" => ControllerKeys.DpadLeft,
                "dpadright" => ControllerKeys.DpadRight,
                "lstick" => ControllerKeys.LStick,
                "rstick" => ControllerKeys.RStick,
                _ => throw new ArgumentException($"Unknown button '{button}'")
            };

            int hold = Math.Max(duration, 16);
            lock (_inputLock)
            {
                _heldButtons = key;
                _buttonsUntilMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + hold;
            }

            _ = Task.Delay(hold).ContinueWith(_ =>
            {
                lock (_inputLock)
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _buttonsUntilMs)
                    {
                        _heldButtons = 0;
                    }
                }
            });

            return new { ok = true, pressed = button, durationMs = duration };
        }

        private static object JoystickSet(JsonElement args)
        {
            float dx = args.TryGetProperty("dx", out JsonElement x) ? (float)x.GetDouble() : 0f;
            float dy = args.TryGetProperty("dy", out JsonElement y) ? (float)y.GetDouble() : 0f;
            int duration = args.TryGetProperty("durationMs", out JsonElement d) ? d.GetInt32() : 300;

            dx = Math.Clamp(dx, -1f, 1f);
            dy = Math.Clamp(dy, -1f, 1f);

            // NpadManager expects axis in [-32768, 32767] after driver scaling; drive via
            // device.Hid.Npads.Update with a stick magnitude directly.
            _ = Task.Run(async () =>
            {
                var device = _vm?.AppHost?.Device;
                if (device == null) return;

                var input = new GamepadInput { PlayerId = PlayerIndex.Player1 };
                input.LStick.Dx = (int)(dx * short.MaxValue);
                input.LStick.Dy = (int)(dy * short.MaxValue);

                long until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(duration, 16);
                while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < until)
                {
                    device.Hid.Npads.Update(new[] { input });
                    await Task.Delay(16);
                }
                device.Hid.Npads.Update(new[] { new GamepadInput { PlayerId = PlayerIndex.Player1 } });
            });

            return new { ok = true, dx, dy, durationMs = duration };
        }

        private static object TouchTap(JsonElement args)
        {
            float x = args.TryGetProperty("x", out JsonElement px) ? (float)px.GetDouble() : 0.5f;
            float y = args.TryGetProperty("y", out JsonElement py) ? (float)py.GetDouble() : 0.5f;
            return TouchRaw(x, y);
        }

        private static object MouseMove(JsonElement args)
        {
            float x = args.TryGetProperty("x", out JsonElement px) ? (float)px.GetDouble() : 0.5f;
            float y = args.TryGetProperty("y", out JsonElement py) ? (float)py.GetDouble() : 0.5f;
            return TouchRaw(x, y);
        }

        private static object TouchRaw(float xNorm, float yNorm)
        {
            var device = _vm?.AppHost?.Device;
            if (device == null)
            {
                return RpcError("no_game", "No game is running");
            }

            xNorm = Math.Clamp(xNorm, 0f, 1f);
            yNorm = Math.Clamp(yNorm, 0f, 1f);

            uint tx = (uint)(xNorm * PanelWidth);
            uint ty = (uint)(yNorm * PanelHeight);

            var start = new TouchPoint
            {
                Attribute = TouchAttribute.Start,
                X = tx,
                Y = ty,
                DiameterX = 10,
                DiameterY = 10,
                Angle = 90,
            };
            var end = start;
            end.Attribute = TouchAttribute.End;

            device.Hid.Touchscreen.Update(start);

            _ = Task.Delay(80).ContinueWith(_ =>
            {
                try
                {
                    var d = _vm?.AppHost?.Device;
                    d?.Hid.Touchscreen.Update(end);
                }
                catch { }
            });

            return new { ok = true, x = tx, y = ty };
        }

        private static object Screenshot()
        {
            var appHost = _vm?.AppHost;
            if (appHost?.Device?.Processes?.ActiveApplication == null)
            {
                return RpcError("no_game", "No game is running");
            }

            var tcs = new TaskCompletionSource<string>();
            EventHandler<ScreenCaptureImageInfo> handler = null;
            handler = (sender, e) =>
            {
                try
                {
                    string png = EncodePngBase64(e);
                    tcs.TrySetResult(png);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            appHost.ScreenshotCaptured += handler;

            try
            {
                // The render thread consumes ScreenshotRequested once per frame; if the flag
                // races with a frame boundary it can be swallowed, so re-assert it repeatedly.
                DateTime deadline = DateTime.UtcNow.AddSeconds(4);
                while (!tcs.Task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    appHost.ScreenshotRequested = true;
                    Thread.Sleep(100);
                }

                if (!tcs.Task.IsCompleted)
                {
                    return RpcError("timeout", "Screenshot timed out");
                }
                return new { ok = true, image = "data:image/png;base64," + tcs.Task.Result };
            }
            finally
            {
                appHost.ScreenshotCaptured -= handler;
                appHost.ScreenshotRequested = false;
            }
        }

        private static string EncodePngBase64(ScreenCaptureImageInfo e)
        {
            if (e.Data == null || e.Data.Length == 0 || e.Width <= 0 || e.Height <= 0)
            {
                throw new InvalidOperationException("Empty screenshot data");
            }

            SKColorType colorType = e.IsBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
            using var bitmap = new SKBitmap(new SKImageInfo(e.Width, e.Height, colorType, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(e.Data, 0, bitmap.GetPixels(), e.Data.Length);

            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (png == null) throw new InvalidOperationException("PNG encode failed");
            return Convert.ToBase64String(png.ToArray());
        }

        private static object RpcError(string code, string message)
        {
            return new { error = new { code, message } };
        }

        private static void WriteJson(HttpListenerContext ctx, int status, object payload)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
