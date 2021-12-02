using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Deceive.Properties;

namespace Deceive
{
    public class MainController : ApplicationContext
    {
        private readonly string _statusFile = Path.Combine(Utils.DataDir, "status");
        private readonly NotifyIcon _trayIcon;
        private bool _connected;
        private bool _connectToMuc = true;
        private bool _createdFakePlayer;
        private bool _enabled = true;

        private SslStream _incoming;
        private string _lastPresence; // we resend this if the state changes
        private SslStream _outgoing;
        private bool _sentIntroductionText;
        private string _status;

        public MainController()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = Resources.DeceiveIcon,
                Visible = true,
                BalloonTipTitle = StartupHandler.DeceiveTitle,
                BalloonTipText = "Deceive is currently masking your status. Right-click the tray icon for more options."
            };
            _trayIcon.ShowBalloonTip(5000);

            LoadStatus();
            UpdateTray();
        }

        public event EventHandler ConnectionErrored;

        private void UpdateTray()
        {
            var aboutMenuItem = new ToolStripMenuItem(StartupHandler.DeceiveTitle)
            {
                Enabled = false
            };

            var enabledMenuItem = new ToolStripMenuItem("Enabled", null, (a, e) =>
            {
                _enabled = !_enabled;
                UpdateStatus(_enabled ? _status : "chat");
                UpdateTray();
            })
            {
                Checked = _enabled
            };

            var mucMenuItem = new ToolStripMenuItem("Enable lobby chat", null, (a, e) =>
            {
                _connectToMuc = !_connectToMuc;
                UpdateTray();
            })
            {
                Checked = _connectToMuc
            };

            var chatStatus = new ToolStripMenuItem("Online", null, (a, e) =>
            {
                UpdateStatus(_status = "chat");
                _enabled = true;
                UpdateTray();
            })
            {
                Checked = _status.Equals("chat")
            };

            var offlineStatus = new ToolStripMenuItem("Offline", null, (a, e) =>
            {
                UpdateStatus(_status = "offline");
                _enabled = true;
                UpdateTray();
            })
            {
                Checked = _status.Equals("offline")
            };

            var mobileStatus = new ToolStripMenuItem("Mobile", null, (a, e) =>
            {
                UpdateStatus(_status = "mobile");
                _enabled = true;
                UpdateTray();
            })
            {
                Checked = _status.Equals("mobile")
            };

            var typeMenuItem = new ToolStripMenuItem("Status Type", null, chatStatus, offlineStatus, mobileStatus);

            var quitMenuItem = new ToolStripMenuItem("Quit", null, (a, b) =>
            {
                var result = MessageBox.Show(
                    "Are you sure you want to stop Deceive? This will also stop related games if they are running.",
                    StartupHandler.DeceiveTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1
                );

                if (result != DialogResult.Yes)
                    return;

                Utils.KillProcesses();
                SaveStatus();
                Application.Exit();
            });

            var closeIn = new ToolStripMenuItem("Close incoming", null, (a, e) => { _incoming.Close(); });
            var closeOut = new ToolStripMenuItem("Close outgoing", null, (a, e) => { _outgoing.Close(); });

            _trayIcon.ContextMenuStrip = new ContextMenuStrip();
            _trayIcon.ContextMenuStrip.Items.AddRange(new[]
            {
                aboutMenuItem, enabledMenuItem, typeMenuItem, mucMenuItem, quitMenuItem
            });
        }

        public void StartThreads(SslStream incoming, SslStream outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _connected = true;
            _createdFakePlayer = false;

            new Thread(IncomingLoop).Start();
            new Thread(OutgoingLoop).Start();
        }

        private void IncomingLoop()
        {
            try
            {
                int byteCount;
                byte[] bytes = new byte[8192];

                do
                {
                    byteCount = _incoming.Read(bytes, 0, bytes.Length);

                    string content = Encoding.UTF8.GetString(bytes, 0, byteCount);

                    // If this is possibly a presence stanza, rewrite it.
                    if (content.Contains("<presence") && _enabled)
                    {
                        PossiblyRewriteAndResendPresence(content, _status);
                        Trace.WriteLine("<!--RC TO SERVER ORIGINAL-->" + content);
                    }
                    else if (content.Contains("41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net"))
                    {
                        //Don't send anything involving our fake user to chat servers
                        Trace.WriteLine("<!--RC TO SERVER REMOVED-->" + content);
                    }
                    else
                    {
                        _outgoing.Write(bytes, 0, byteCount);
                        Trace.WriteLine("<!--RC TO SERVER-->" + content);
                    }
                } while (byteCount != 0 && _connected);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
            }
            finally
            {
                Trace.WriteLine(@"Incoming closed.");
                SaveStatus();
                if (_connected)
                    OnConnectionErrored();
            }
        }

        private void OutgoingLoop()
        {
            try
            {
                int byteCount;
                byte[] bytes = new byte[8192];

                do
                {
                    byteCount = _outgoing.Read(bytes, 0, bytes.Length);
                    _incoming.Write(bytes, 0, byteCount);
                    Trace.WriteLine("<!--SERVER TO RC-->" + Encoding.UTF8.GetString(bytes, 0, byteCount));
                } while (byteCount != 0 && _connected);

                Trace.WriteLine(@"Outgoing closed.");
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                Trace.WriteLine(@"Outgoing errored.");
                SaveStatus();
                if (_connected)
                    OnConnectionErrored();
            }
        }

        private void PossiblyRewriteAndResendPresence(string content, string targetStatus)
        {
            try
            {
                _lastPresence = content;
                string wrappedContent = "<xml>" + content + "</xml>";
                var xml = XDocument.Load(new StringReader(wrappedContent));

                if (xml.Root == null)
                    return;
                if (xml.Root.HasElements == false)
                    return;

                foreach (var presence in xml.Root.Elements())
                {
                    if (presence.Name != "presence")
                        continue;
                    if (presence.Attribute("to") != null)
                    {
                        if (_connectToMuc)
                            continue;
                        presence.Remove();
                    }

                    if (!_createdFakePlayer)
                        CreateFakePlayer();

                    if (targetStatus != "chat" ||
                        presence.Element("games")?.Element("league_of_legends")?.Element("st")?.Value != "dnd")
                    {
                        presence.Element("show")?.ReplaceNodes(targetStatus);
                        presence.Element("games")?.Element("league_of_legends")?.Element("st")
                            ?.ReplaceNodes(targetStatus);
                    }

                    if (targetStatus == "chat")
                        continue;
                    presence.Element("status")?.Remove();

                    if (targetStatus == "mobile")
                    {
                        presence.Element("games")?.Element("league_of_legends")?.Element("p")?.Remove();
                        presence.Element("games")?.Element("league_of_legends")?.Element("m")?.Remove();
                    }
                    else
                    {
                        presence.Element("games")?.Element("league_of_legends")?.Remove();
                    }

                    //Remove Legends of Runeterra presence
                    presence.Element("games")?.Element("bacon")?.Remove();

                    //Remove VALORANT presence
                    presence.Element("games")?.Element("valorant")?.Remove();
                }

                var sb = new StringBuilder();
                var xws = new XmlWriterSettings
                    {OmitXmlDeclaration = true, Encoding = Encoding.UTF8, ConformanceLevel = ConformanceLevel.Fragment};
                using (var xw = XmlWriter.Create(sb, xws))
                {
                    foreach (var xElement in xml.Root.Elements())
                        xElement.WriteTo(xw);
                }

                _outgoing.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                Trace.WriteLine("<!--DECEIVE TO SERVER-->" + sb);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                Trace.WriteLine(@"Error rewriting presence.");
            }
        }

        private async void CreateFakePlayer()
        {
            return;

            _createdFakePlayer = true;

            const string subscriptionMessage =
                "<iq from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net' id='fake-player' type='set'>" +
                "<query xmlns='jabber:iq:riotgames:roster'>" +
                "<item jid='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net' name='&#9;Deceive Active!' subscription='both' puuid='41c322a1-b328-495b-a004-5ccd3e45eae8'>" +
                "<group priority='9999'>Deceive</group>" +
                "<id name='&#9;Deceive Active!' tagline=''/> <lol name='&#9;Deceive Active!'/>" +
                "</item>" +
                "</query>" +
                "</iq>";

            const string presenceMessage =
                "<presence from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-Deceive' id='fake-player-2'>" +
                "<games>" +
                "<keystone><st>chat</st><s.p>keystone</s.p></keystone>" +
                "<league_of_legends><st>chat</st><s.p>league_of_legends</s.p><p>{&quot;pty&quot;:true}</p></league_of_legends>" + // No Region s.r keeps it in the main "League" category rather than "Other Servers" in every region with "Group Games & Servers" active 
                "<valorant><st>chat</st><s.p>valorant</s.p><p>ewoJImlzVmFsaWQiOiB0cnVlLAoJInBhcnR5SWQiOiAiMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAwIiwKCSJwYXJ0eUNsaWVudFZlcnNpb24iOiAicmVsZWFzZS0wMS4wNS1zaGlwcGluZy0xMC00NjAxMjkiCn0=</p></valorant>" +
                "<bacon><st>chat</st><s.l>bacon_availability_online</s.l><s.p>bacon</s.p><s.t>1596633825489</s.t></bacon>" + // Timestamp needed or it will show offline
                "</games>" +
                "<show>chat</show>" +
                "</presence>";

            byte[] bytes = Encoding.UTF8.GetBytes(subscriptionMessage);
            _incoming.Write(bytes, 0, bytes.Length);
            Trace.WriteLine("<!--DECEIVE TO RC-->" + subscriptionMessage);

            await Task.Delay(200);

            bytes = Encoding.UTF8.GetBytes(presenceMessage);
            _incoming.Write(bytes, 0, bytes.Length);
            Trace.WriteLine("<!--DECEIVE TO RC-->" + presenceMessage);

            await Task.Delay(10000);

            if (_sentIntroductionText)
                return;
            _sentIntroductionText = true;

            SendMessageFromFakePlayer("Welcome! Deceive is running and you are currently appearing " + _status +
                                      ". Despite what the game client may indicate, you are appearing offline to your friends unless you manually disable Deceive.");
            await Task.Delay(200);
            SendMessageFromFakePlayer(
                "If you want to invite others while being offline, you may need to disable Deceive for them to accept. You can enable Deceive again as soon as they are in your lobby.");
            await Task.Delay(200);
            SendMessageFromFakePlayer(
                "To enable or disable Deceive, or to configure other settings, find Deceive in your tray icons.");
            await Task.Delay(200);
            SendMessageFromFakePlayer("Have fun!");
        }

        private void SendMessageFromFakePlayer(string message)
        {
            return;

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");

            string chatMessage =
                $"<message from='41c322a1-b328-495b-a004-5ccd3e45eae8@eu1.pvp.net/RC-Deceive' stamp='{stamp}' id='fake-{stamp}' type='chat'><body>{message}</body></message>";

            byte[] bytes = Encoding.UTF8.GetBytes(chatMessage);
            _incoming.Write(bytes, 0, bytes.Length);
            Trace.WriteLine("<!--DECEIVE TO RC-->" + chatMessage);
        }

        private void UpdateStatus(string newStatus)
        {
            if (string.IsNullOrEmpty(_lastPresence))
                return;

            PossiblyRewriteAndResendPresence(_lastPresence, newStatus);

            if (newStatus == "chat")
                SendMessageFromFakePlayer("You are now appearing online.");
            else
                SendMessageFromFakePlayer("You are now appearing " + newStatus + ".");
        }

        private void LoadStatus()
        {
            if (File.Exists(_statusFile))
                _status = File.ReadAllText(_statusFile) == "mobile" ? "mobile" : "offline";
            else
                _status = "offline";
        }

        private void SaveStatus()
        {
            File.WriteAllText(_statusFile, _status);
        }

        private void OnConnectionErrored()
        {
            _connected = false;
            ConnectionErrored?.Invoke(this, EventArgs.Empty);
        }
    }
}
