using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    internal class MainController : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private bool _enabled = true;
        private string _status;
        private readonly string _statusFile = Path.Combine(Utils.DataDir, "status");
        private bool _connectToMuc = true;

        private LCUOverlay _overlay = null;
        private WindowFollower _follower = null;

        private SslStream _incoming;
        private SslStream _outgoing;
        private string _lastPresence; // we resend this if the state changes

        internal MainController(bool createOverlay)
        {
            _trayIcon = new NotifyIcon
            {
                Icon = Resources.deceive,
                Visible = true,
                BalloonTipTitle = StartupHandler.DeceiveTitle,
                BalloonTipText = "Deceive is currently masking your status. Right-Click the tray icon for more options."
            };
            _trayIcon.ShowBalloonTip(5000);

            // Create overlay and start following the LCU with it.
            if (createOverlay) CreateOverlay();

            LoadStatus();
            UpdateUI();
        }

        private async void CreateOverlay()
        {
            while (true)
            {
                var process = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
                if (process == null)
                {
                    await Task.Delay(5000);
                    continue;
                }

                _overlay = new LCUOverlay();
                _overlay.Show();
                _follower = new WindowFollower(_overlay, process);
                _follower.StartFollowing();
                return;
            }
        }

        private void UpdateUI()
        {
            var aboutMenuItem = new MenuItem(StartupHandler.DeceiveTitle)
            {
                Enabled = false
            };

            var enabledMenuItem = new MenuItem("Enabled", (a, e) =>
            {
                _enabled = !_enabled;
                UpdateStatus(_enabled ? _status : "chat");
                UpdateUI();
            })
            {
                Checked = _enabled
            };

            var overlayMenuItem = new MenuItem("Show status overlay", (a, e) =>
            {
                if (_overlay == null)
                {
                    CreateOverlay();
                }
                else
                {
                    _follower.Dispose();
                    _overlay.Close();
                    _overlay = null;
                }

                UpdateUI();
            })
            {
                Checked = _overlay != null
            };

            var mucMenuItem = new MenuItem("Enable lobby chat", (a, e) =>
            {
                _connectToMuc = !_connectToMuc;
                UpdateUI();
            })
            {
                Checked = _connectToMuc
            };

            var offlineStatus = new MenuItem("Offline", (a, e) =>
            {
                UpdateStatus(_status = "offline");
                _enabled = true;
                UpdateUI();
            })
            {
                Checked = _status.Equals("offline")
            };

            var mobileStatus = new MenuItem("Mobile", (a, e) =>
            {
                UpdateStatus(_status = "mobile");
                _enabled = true;
                UpdateUI();
            })
            {
                Checked = _status.Equals("mobile")
            };

            var typeMenuItem = new MenuItem("Status Type", new[] {offlineStatus, mobileStatus});

            var quitMenuItem = new MenuItem("Quit", (a, b) =>
            {
                var result = MessageBox.Show(
                    "Are you sure you want to stop Deceive? This will also stop League if it is running.",
                    StartupHandler.DeceiveTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1
                );

                if (result != DialogResult.Yes) return;

                Utils.KillClients();
                SaveStatus();
                Application.Exit();
            });

            _trayIcon.ContextMenu = new ContextMenu(new[] {aboutMenuItem, enabledMenuItem, typeMenuItem, overlayMenuItem, mucMenuItem, quitMenuItem});
            _overlay?.UpdateStatus(_status, _enabled);
        }

        public void StartThreads(SslStream incoming, SslStream outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;

            new Thread(IncomingLoop).Start();
            new Thread(OutgoingLoop).Start();
        }

        private void IncomingLoop()
        {
            try
            {
                int byteCount;
                var bytes = new byte[4096];

                do
                {
                    byteCount = _incoming.Read(bytes, 0, bytes.Length);

                    var content = Encoding.UTF8.GetString(bytes, 0, byteCount);
                    Debug.WriteLine("FROM RC: " + content);

                    // If this is possibly a presence stanza, rewrite it.
                    if (content.Contains("<presence") && _enabled)
                    {
                        PossiblyRewriteAndResendPresence(content, _status);
                    }
                    else
                    {
                        _outgoing.Write(bytes, 0, byteCount);
                    }
                } while (byteCount != 0);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
            }
            finally
            {
                Trace.WriteLine(@"Incoming closed.");
                SaveStatus();
                Application.Exit();
            }
        }

        private void OutgoingLoop()
        {
            try
            {
                int byteCount;
                var bytes = new byte[4096];

                do
                {
                    byteCount = _outgoing.Read(bytes, 0, bytes.Length);
                    Debug.WriteLine("TO RC: " + Encoding.UTF8.GetString(bytes, 0, byteCount));
                    _incoming.Write(bytes, 0, byteCount);
                } while (byteCount != 0);

                Trace.WriteLine(@"Outgoing closed.");
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                Trace.WriteLine(@"Outgoing errored.");
                SaveStatus();
                Application.Exit();
            }
        }

        private void PossiblyRewriteAndResendPresence(string content, string targetStatus)
        {
            try
            {
                _lastPresence = content;
                var wrappedContent = "<xml>" + content + "</xml>";
                var xml = XDocument.Load(new StringReader(wrappedContent));

                if (xml.Root == null) return;
                if (xml.Root.HasElements == false) return;

                foreach (var presence in xml.Root.Elements())
                {
                    if (presence.Name != "presence") continue;
                    if (presence.Attribute("to") != null)
                    {
                        if (_connectToMuc) continue;
                        presence.Remove();
                    }

                    presence.Element("show")?.ReplaceNodes(targetStatus);

                    if (targetStatus == "chat") continue;
                    presence.Element("status")?.Remove();

                    if (targetStatus == "mobile")
                    {
                        presence.Element("games")?.Element("league_of_legends")?.Element("p")?.Remove();
                        presence.Element("games")?.Element("league_of_legends")?.Element("m")?.Remove();
                        presence.Element("games")?.Element("league_of_legends")?.Element("st")?.ReplaceNodes(targetStatus);
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
                var xws = new XmlWriterSettings {OmitXmlDeclaration = true, Encoding = Encoding.UTF8, ConformanceLevel = ConformanceLevel.Fragment};
                using (var xw = XmlWriter.Create(sb, xws))
                {
                    foreach (var xElement in xml.Root.Elements())
                    {
                        xElement.WriteTo(xw);
                    }
                }

                _outgoing.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                Debug.WriteLine("DECEIVE: " + sb);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                Trace.WriteLine(@"Error rewriting presence.");
            }
        }

        private void UpdateStatus(string newStatus)
        {
            if (string.IsNullOrEmpty(_lastPresence)) return;

            PossiblyRewriteAndResendPresence(_lastPresence, newStatus);
        }

        private void LoadStatus()
        {
            if (File.Exists(_statusFile)) _status = File.ReadAllText(_statusFile) == "mobile" ? "mobile" : "offline";
            else _status = "offline";
        }

        private void SaveStatus()
        {
            File.WriteAllText(_statusFile, _status);
        }
    }
}