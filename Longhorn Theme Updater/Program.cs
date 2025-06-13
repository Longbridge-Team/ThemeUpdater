/*---------------------------------------------------------
  LONGHORN THEME MANAGER - Because your desktop deserves better
  Version: 2.0 "Hillel Edition"
  (c) 2025 Flarf - Project Longbridge™
  All rights reserved. Unauthorized use will make Bill cry.
  ---------------------------------------------------------*/

using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Generic;

namespace LonghornThemeManager
{
    // Native methods - because P/Invoke is where the real magic happens
    public static class NativeMethods
    {
        [DllImport("LongbridgeThemeCore.dll", EntryPoint = "themetool_signature_fix", CharSet = CharSet.Unicode)]
        public static extern int ThemeTool_SignatureFix(string path);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);
        public const int SPI_SETDESKWALLPAPER = 0x0014;
        public const int SPIF_UPDATEINIFILE = 0x01;
        public const int SPIF_SENDCHANGE = 0x02;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int SetErrorMode(int wMode);
        public const int SEM_FAILCRITICALERRORS = 0x0001;
        public const int SEM_NOGPFAULTERRORBOX = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadErrorMode(int dwNewMode, out int lpOldMode);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll")]
        public static extern int EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    }

    // ThemeCore - The engine that makes this whole clusterfuck work
    internal static class ThemeCore
    {
        [DllImport("LongbridgeThemeCore.dll", EntryPoint = "ThemeTool_Init", CharSet = CharSet.Unicode)]
        public static extern int ThemeTool_Init();

        [DllImport("LongbridgeThemeCore.dll", EntryPoint = "SecureUxTheme_Install", CharSet = CharSet.Unicode)]
        public static extern int SecureUxTheme_Install(string themePath);

        [DllImport("LongbridgeThemeCore.dll", EntryPoint = "ThemeTool_SetActive", CharSet = CharSet.Unicode)]
        public static extern int ThemeTool_SetActive(string themePath);

        public static string ExtractWallpaperPathFromTheme(string themePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(themePath);
                foreach (string line in lines)
                {
                    if (line.StartsWith("Wallpaper=", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = line.Substring("Wallpaper=".Length).Trim();
                        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                    }
                }
            }
            catch { }
            return null;
        }

        public static void ApplyTheme(string themePath)
        {
            int oldMode;
            NativeMethods.SetThreadErrorMode(NativeMethods.SEM_FAILCRITICALERRORS | NativeMethods.SEM_NOGPFAULTERRORBOX, out oldMode);

            try
            {
                int initResult = ThemeTool_Init();
                if (initResult != 0)
                    throw new Exception($"Init failed: 0x{initResult:X8}");

                int activateResult = ThemeTool_SetActive(themePath);
                if (activateResult != 0 && activateResult != 0x800700B7)
                    throw new Exception($"Activation failed: 0x{activateResult:X8}");

                BroadcastThemeChange();

                string wallpaperPath = ExtractWallpaperPathFromTheme(themePath);
                if (!string.IsNullOrEmpty(wallpaperPath) && File.Exists(wallpaperPath))
                {
                    NativeMethods.SystemParametersInfo(
                        NativeMethods.SPI_SETDESKWALLPAPER,
                        0,
                        wallpaperPath,
                        NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"ThemeCore.ApplyTheme failed: {ex.Message}", ex);
            }
            finally
            {
                new Thread(() => {
                    Thread.Sleep(10000);
                    NativeMethods.SetThreadErrorMode(oldMode, out _);
                }).Start();
            }
        }

        private static void BroadcastThemeChange()
        {
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint WM_THEMECHANGED = 0x031A;
            const uint SMTO_ABORTIFHUNG = 0x0002;

            try
            {
                IntPtr result;
                NativeMethods.SendMessageTimeout(
                    (IntPtr)0xFFFF,
                    WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    "Policy",
                    SMTO_ABORTIFHUNG,
                    1000,
                    out result);

                NativeMethods.SendMessageTimeout(
                    (IntPtr)0xFFFF,
                    WM_THEMECHANGED,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    SMTO_ABORTIFHUNG,
                    1000,
                    out result);
            }
            catch { }
        }
    }

    // Program - The thing that makes this shit run
    static class Program
    {
        private static Mutex singleInstanceMutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, "LonghornThemeManagerSingleton", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("A window of the theme manager is already open.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!IsAdministrator())
            {
                if (ElevateToAdmin())
                {
                    return;
                }
            }

            Application.Run(new MainForm());
            GC.KeepAlive(singleInstanceMutex);
        }

        private static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool ElevateToAdmin()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Environment.CurrentDirectory
                };

                Process process = Process.Start(startInfo);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // MainForm - Where the magic happens (and sometimes crashes)
    public partial class MainForm : Form
    {
        private const string ThemesPath = @"C:\Windows\Resources\Themes\";
        private const string CustomThemesPath = @"C:\Windows\Resources\Themes\Custom\";
        private const string StartIsBackKey = @"Software\StartIsBack";

        // Theme Tab Controls
        private ComboBox themeComboBox;
        private Button applyButton;
        private Button previewButton;
        private Label statusLabel;
        private Label currentThemeLabel;
        private PictureBox wallpaperPreview;
        private Label themeDetailsLabel;
        private PictureBox taskbarPreview;

        // User Theme Tab Controls
        private Button btnSetTaskbarColor;
        private TrackBar tbTransparency;
        private Button btnSetWallpaper;
        private Button btnSaveTheme;
        private ComboBox customThemeComboBox;
        private Button btnApplyCustomTheme;
        private PictureBox pbUserWallpaperPreview;
        private PictureBox pbUserTaskbarPreview;
        private ColorDialog colorDialog;
        private Label lblTransparencyValue;

        // User theme settings
        private Color userTaskbarColor = Color.Black;
        private int userAlpha = 255;
        private string userWallpaperPath = "";

        // UI Containers
        private Panel headerPanel;
        private Panel footerPanel;
        private TabControl mainTabControl;
        private TabPage tabPageThemes;
        private TabPage tabPageUserThemes;
        private TabPage tabPageStartMenu;  // Changed from OpenShell to Start Menu

        // Start Menu Tab Controls (formerly Open Shell)
        private ComboBox openShellStyleComboBox;
        private ComboBox openShellSkinComboBox;
        private Button btnApplyOpenShellSkin;
        private Label lblOpenShellStatus;
        private Button btnRefreshSkins;

        // Start Menu settings
        private const string OpenShellKey = @"Software\OpenShell\StartMenu\Settings";
        private const string OpenShellSkinsPath = @"C:\Program Files\Open-Shell\Skins\";
        private string currentOpenShellStyle = "Win7";
        private string currentOpenShellSkin = "";

        // Constructor - Where we pretend to know what we're doing
        public MainForm()
        {
            InitializeUI();
            this.Text = "Microsoft® Windows Longhorn™ Theme Manager";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimumSize = new Size(650, 550);
            this.Size = new Size(650, 550);
            this.BackColor = SystemColors.Control;
        }

        // InitializeUI - Because someone thought WinForms was still cool in 2025
        private void InitializeUI()
        {
            Font = new Font("Tahoma", 8.25F);

            // Header Panel - Where we put the shiny stuff
            headerPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(this.ClientSize.Width, 60),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            headerPanel.Paint += (sender, e) =>
            {
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    headerPanel.ClientRectangle,
                    Color.FromArgb(0, 0, 128),
                    Color.FromArgb(16, 132, 208),
                    90F))
                {
                    e.Graphics.FillRectangle(brush, headerPanel.ClientRectangle);
                }
            };
            headerPanel.Resize += (s, e) => headerPanel.Invalidate();

            // Logo and title - Because branding is everything
            var logoPictureBox = new PictureBox
            {
                Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(),
                Location = new Point(10, 10),
                Size = new Size(32, 32),
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Transparent
            };
            var titleLabel = new Label
            {
                Text = "Microsoft® Windows Longhorn™ Theme Manager",
                Font = new Font("Tahoma", 14F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(50, 15),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            headerPanel.Controls.Add(logoPictureBox);
            headerPanel.Controls.Add(titleLabel);

            // Main Tab Control - Where we hide all the complexity
            mainTabControl = new TabControl
            {
                Location = new Point(10, 70),
                Size = new Size(ClientSize.Width - 20, ClientSize.Height - 130),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // Create Theme Tab
            tabPageThemes = new TabPage("Themes")
            {
                AutoScroll = true,
                Padding = new Padding(10)
            };
            InitializeThemeTab();
            mainTabControl.TabPages.Add(tabPageThemes);

            // Create User Theme Tab
            tabPageUserThemes = new TabPage("User Themes")
            {
                AutoScroll = true,
                Padding = new Padding(10)
            };
            InitializeUserThemeTab();
            mainTabControl.TabPages.Add(tabPageUserThemes);

            // Create Start Menu Tab (formerly Open Shell) - Because rebranding is easier than fixing bugs
            tabPageStartMenu = new TabPage("Start Menu")  // Changed tab name
            {
                AutoScroll = true,
                Padding = new Padding(10)
            };
            InitializeStartMenuTab();
            mainTabControl.TabPages.Add(tabPageStartMenu);

            // Footer Panel - Where we put the boring stuff
            footerPanel = new Panel
            {
                Height = 50,
                Dock = DockStyle.Bottom,
                BackColor = SystemColors.Control
            };

            statusLabel = new Label
            {
                Text = "Ready to apply theme",
                Location = new Point(10, 5),
                AutoSize = true,
                Font = new Font("Tahoma", 8.25F),
                ForeColor = SystemColors.ControlDarkDark
            };

            var copyrightLabel = new Label
            {
                Text = "© 2025 Flarf. Project Longbridge™",
                Location = new Point(footerPanel.Width - 250, 5),
                AutoSize = true,
                Font = new Font("Tahoma", 8.25F, FontStyle.Regular),
                ForeColor = SystemColors.ControlDarkDark,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };

            footerPanel.Controls.Add(statusLabel);
            footerPanel.Controls.Add(copyrightLabel);

            this.Controls.Add(headerPanel);
            this.Controls.Add(mainTabControl);
            this.Controls.Add(footerPanel);

            mainTabControl.SelectedIndexChanged += MainTabControl_SelectedIndexChanged;
        }

        // InitializeStartMenuTab - Where we pretend Open Shell is still relevant
        private void InitializeStartMenuTab()
        {
            // Style selection - Because everyone loves dropdowns
            var lblStyle = new Label
            {
                Text = "Menu Style:",
                Location = new Point(20, 20),
                AutoSize = true,
                Font = new Font("Tahoma", 9F),
                BackColor = Color.Transparent
            };

            openShellStyleComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, 15),
                Size = new Size(200, 21),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };

            // Updated menu styles - removed 1 column because it sucked anyway
            openShellStyleComboBox.Items.AddRange(new string[] {
                "Windows 7 Style",      // The one true style
                "Windows XP Style"      // Because nostalgia sells
            });
            openShellStyleComboBox.SelectedIndexChanged += OpenShellStyleComboBox_SelectedIndexChanged;

            // Skin selection - Because appearances matter
            var lblSkin = new Label
            {
                Text = "Select Skin:",
                Location = new Point(20, 60),
                AutoSize = true,
                Font = new Font("Tahoma", 9F),
                BackColor = Color.Transparent
            };

            openShellSkinComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, 55),
                Size = new Size(250, 21),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };

            // Apply button - The button we all click and pray
            btnApplyOpenShellSkin = new Button
            {
                Text = "Apply Skin",
                Location = new Point(380, 55),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.System
            };
            btnApplyOpenShellSkin.Click += BtnApplyOpenShellSkin_Click;

            // Refresh button - For when things inevitably break
            btnRefreshSkins = new Button
            {
                Text = "Refresh Skins",
                Location = new Point(490, 55),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.System
            };
            btnRefreshSkins.Click += (s, e) => PopulateOpenShellSkins();

            // Status label - Where we display our excuses
            lblOpenShellStatus = new Label
            {
                Location = new Point(20, 90),
                Size = new Size(560, 60),
                Font = new Font("Tahoma", 8.25F),
                ForeColor = SystemColors.ControlDarkDark
            };

            tabPageStartMenu.Controls.Add(lblStyle);
            tabPageStartMenu.Controls.Add(openShellStyleComboBox);
            tabPageStartMenu.Controls.Add(lblSkin);
            tabPageStartMenu.Controls.Add(openShellSkinComboBox);
            tabPageStartMenu.Controls.Add(btnApplyOpenShellSkin);
            tabPageStartMenu.Controls.Add(btnRefreshSkins);
            tabPageStartMenu.Controls.Add(lblOpenShellStatus);
        }

        // OpenShellStyleComboBox_SelectedIndexChanged - Because users change their minds
        private void OpenShellStyleComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Updated style mapping - RIP Classic 1 Column, we hardly knew ye
            currentOpenShellStyle = openShellStyleComboBox.SelectedIndex switch
            {
                0 => "Win7",       // The good one
                1 => "Classic2",   // The one we call "Windows XP Style" to make it sound better
                _ => "Win7"        // Fallback because users are unpredictable
            };

            // Refresh skin list for the new style
            PopulateOpenShellSkins();
        }

        // PopulateOpenShellSkins - Where we search for skins like it's 2005
        private void PopulateOpenShellSkins()
        {
            openShellSkinComboBox.Items.Clear();
            lblOpenShellStatus.Text = "Scanning for skins...";
            Application.DoEvents();

            try
            {
                if (Directory.Exists(OpenShellSkinsPath))
                {
                    // Filter by current style
                    string extension = currentOpenShellStyle == "Win7" ? "*.skin7" : "*.skin";

                    var skinFiles = Directory.GetFiles(OpenShellSkinsPath, extension)
                        .Select(Path.GetFileNameWithoutExtension)
                        .Distinct()
                        .ToArray();

                    if (skinFiles.Length > 0)
                    {
                        openShellSkinComboBox.Items.AddRange(skinFiles);

                        // Select current skin if available
                        if (!string.IsNullOrEmpty(currentOpenShellSkin) &&
                            openShellSkinComboBox.Items.Contains(currentOpenShellSkin))
                        {
                            openShellSkinComboBox.SelectedItem = currentOpenShellSkin;
                        }
                        else if (openShellSkinComboBox.Items.Count > 0)
                        {
                            openShellSkinComboBox.SelectedIndex = 0;
                        }

                        lblOpenShellStatus.Text = $"{skinFiles.Length} {currentOpenShellStyle} skins found. Select a skin and click Apply";
                    }
                    else
                    {
                        lblOpenShellStatus.Text = $"No skin files found ({extension}) for {currentOpenShellStyle} style";
                    }
                }
                else
                {
                    lblOpenShellStatus.Text = "Open Shell skins directory not found: " + OpenShellSkinsPath;
                }
            }
            catch (Exception ex)
            {
                lblOpenShellStatus.Text = "Error loading skins: " + ex.Message;
            }
        }

        // BtnApplyOpenShellSkin_Click - Where we mess with the registry like rebels
        private void BtnApplyOpenShellSkin_Click(object sender, EventArgs e)
        {
            if (openShellSkinComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a skin first", "Selection Required",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string skinName = openShellSkinComboBox.SelectedItem.ToString();
            string style = currentOpenShellStyle;
            string skinValueName = style == "Win7" ? "SkinW7" :
                                 style == "Classic" ? "SkinC1" : // We keep this for compatibility
                                 "SkinC2"; // This is for "Windows XP Style"

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(OpenShellKey, true))
                {
                    // Set menu style
                    key.SetValue("MenuStyle", style, RegistryValueKind.String);

                    // Set skin for the selected style
                    key.SetValue(skinValueName, skinName, RegistryValueKind.String);

                    // Apply skin-specific options
                    if (skinName.Equals("Longhorn Plex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (style == "Win7")
                        {
                            // Apply Longhorn Plex specific options
                            key.SetValue("SkinVariationW7", "Light", RegistryValueKind.String);

                            // Set skin options: Remove top bar + Logo user picture + Small icons
                            string[] plexOptions = {
                                "NO_TOP=1",          // Remove top bar
                                "BRANDED=1",         // Logo user picture
                                "USER_IMAGE=0",      // Not using user picture
                                "USER_IMAGE_OUT=0",  // No user picture offset
                                "NO_IMAGE=0",        // Show image
                                "SMALL_ICONS=1"      // Small icons
                            };
                            key.SetValue("SkinOptionsW7", plexOptions, RegistryValueKind.MultiString);
                        }
                    }

                    else if (skinName.Equals("Longhorn Hillel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (style == "Win7")
                        {
                            // Fixed: Added REDUCE_GLASS option - because Hillel demanded it
                            string[] hillelOptions = {
                                "SMALL_ICONS=1",         // Small icons
                                "LARGE_FONT=0",          // Large font
                                "ARROW=1",               // Arrow
                                "DISABLE_MASK=1",        // Reduce glass color (NEW)
                                "BLACK_TEXT_GLASS=0",    // Black text on glass
                                "INVERT_SEPARATOR=0",    // Invert separator text hue
                                "BLACK_SHUTDOWN_TEXT=0", // Black text on shutdown button
                                "BLACK_BUTTONS=0",       // Black buttons on glass
                                "STONE_ARROW=0",         // Stone 'All Programs' Arrow
                                "FLAT_SELECTORS=0",      // Flat Selectors
                                "SHUTDOWN_GLYPH=0",      // Shutdown Glyph
                                "RUBY_ORB=0",            // Ruby Orb
                                "AMBER_ORB=0"            // Amber Orb
                            };
                            key.SetValue("SkinOptionsW7", hillelOptions, RegistryValueKind.MultiString);
                        }
                    }

                    // Removed setting for SkinC1 (classic 1 column) - because it was garbage
                    // Only set SkinC2 for Windows XP Style
                    if (style == "Classic2")
                    {
                        key.SetValue("SkinC2", skinName, RegistryValueKind.String);
                    }
                }

                currentOpenShellSkin = skinName;
                lblOpenShellStatus.Text = $"Applied skin: {skinName} for {style} style. Restarting Explorer...";
                Application.DoEvents();

                // Restart Explorer to apply changes - because Windows can't do anything live
                RestartExplorer();

                lblOpenShellStatus.Text = "Skin applied successfully! Explorer has been restarted.";
                MessageBox.Show("Skin applied successfully!\n\nExplorer has been restarted to apply changes.",
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblOpenShellStatus.Text = "Error applying skin: " + ex.Message;
                MessageBox.Show($"Error applying skin: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // RestartExplorer - The nuclear option
        private void RestartExplorer()
        {
            try
            {
                lblOpenShellStatus.Text += "\nTerminating Explorer processes...";
                Application.DoEvents();

                var processes = Process.GetProcessesByName("explorer");
                foreach (var process in processes)
                {
                    try
                    {
                        lblOpenShellStatus.Text += $"\nKilling explorer process: {process.Id}";
                        Application.DoEvents();
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    catch (Exception ex)
                    {
                        lblOpenShellStatus.Text += $"\nError killing explorer: {ex.Message}";
                        Application.DoEvents();
                    }
                }

                lblOpenShellStatus.Text += "\nStarting Explorer without windows...";
                Application.DoEvents();
                StartExplorerWithoutWindows();

                lblOpenShellStatus.Text += "\nClosing file explorer windows...";
                Application.DoEvents();
                CloseFileExplorerWindows();

                lblOpenShellStatus.Text += "\nExplorer restart completed";
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                lblOpenShellStatus.Text += $"\nError restarting Explorer: {ex.Message}";
                Application.DoEvents();
            }
        }

        // StartExplorerWithoutWindows - Because we're masochists
        private void StartExplorerWithoutWindows()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c start /min explorer.exe",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            catch
            {
                Process.Start("explorer.exe");
            }
        }

        // CloseFileExplorerWindows - The cleanup crew
        private void CloseFileExplorerWindows()
        {
            try
            {
                Thread.Sleep(3000);

                var processes = Process.GetProcessesByName("explorer");
                foreach (var process in processes)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        StringBuilder className = new StringBuilder(256);
                        NativeMethods.GetClassName(process.MainWindowHandle, className, className.Capacity);

                        if (className.ToString() != "Shell_TrayWnd" &&
                            className.ToString() != "Shell_SecondaryTrayWnd")
                        {
                            NativeMethods.SendMessage(process.MainWindowHandle,
                                                    NativeMethods.WM_CLOSE,
                                                    IntPtr.Zero,
                                                    IntPtr.Zero);
                        }
                    }
                }
            }
            catch { }
        }

        private void MainTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (mainTabControl.SelectedTab == tabPageUserThemes)
            {
                this.PerformLayout();
                UpdateUserPreview();
            }
        }

        // InitializeThemeTab - Where we put the real theming stuff
        private void InitializeThemeTab()
        {
            var themeLabel = new Label
            {
                Text = "Select Theme:",
                Location = new Point(20, 20),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            themeComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, 15),
                Size = new Size(350, 21),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F)
            };
            themeComboBox.SelectedIndexChanged += themeComboBox_SelectedIndexChanged;

            previewButton = new Button
            {
                Text = "Preview Settings",
                Location = new Point(20, 50),
                Size = new Size(150, 30),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F)
            };
            previewButton.Click += previewButton_Click;

            applyButton = new Button
            {
                Text = "Apply Theme",
                Location = new Point(180, 50),
                Size = new Size(150, 30),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F, FontStyle.Bold)
            };
            applyButton.Click += applyButton_Click;

            var previewLabel = new Label
            {
                Text = "Theme Preview:",
                Location = new Point(20, 90),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            wallpaperPreview = new PictureBox
            {
                Location = new Point(20, 110),
                Size = new Size(250, 150),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            themeDetailsLabel = new Label
            {
                Location = new Point(280, 110),
                Size = new Size(tabPageThemes.Width - 300, 150),
                Font = new Font("Tahoma", 8.25F),
                BackColor = Color.White
            };

            var taskbarPreviewLabel = new Label
            {
                Text = "Taskbar Preview:",
                Location = new Point(20, 270),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            taskbarPreview = new PictureBox
            {
                Location = new Point(20, 290),
                Size = new Size(tabPageThemes.Width - 40, 30),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.StretchImage
            };

            var currentSettingsLabel = new Label
            {
                Text = "Current Settings:",
                Location = new Point(20, 330),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            currentThemeLabel = new Label
            {
                Text = "Theme: Not applied",
                Location = new Point(20, 350),
                AutoSize = true,
                BackColor = Color.Transparent
            };

            tabPageThemes.Controls.Add(themeLabel);
            tabPageThemes.Controls.Add(themeComboBox);
            tabPageThemes.Controls.Add(previewButton);
            tabPageThemes.Controls.Add(applyButton);
            tabPageThemes.Controls.Add(previewLabel);
            tabPageThemes.Controls.Add(wallpaperPreview);
            tabPageThemes.Controls.Add(themeDetailsLabel);
            tabPageThemes.Controls.Add(taskbarPreviewLabel);
            tabPageThemes.Controls.Add(taskbarPreview);
            tabPageThemes.Controls.Add(currentSettingsLabel);
            tabPageThemes.Controls.Add(currentThemeLabel);
        }

        // InitializeUserThemeTab - Where users play artist
        private void InitializeUserThemeTab()
        {
            // ============================================
            // Custom Themes Section
            // ============================================
            GroupBox grpCustomThemes = new GroupBox
            {
                Text = "Custom Themes",
                Location = new Point(15, 15),
                Size = new Size(tabPageUserThemes.ClientSize.Width - 30, 100),
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var customThemeLabel = new Label
            {
                Text = "Available Themes:",
                Location = new Point(20, 25),
                AutoSize = true,
                Font = new Font("Tahoma", 9F),
                BackColor = Color.Transparent
            };

            customThemeComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(140, 20),
                Size = new Size(grpCustomThemes.Width - 300, 21),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F)
            };

            btnApplyCustomTheme = new Button
            {
                Text = "Apply Theme",
                Location = new Point(grpCustomThemes.Width - 150, 20),
                Size = new Size(130, 25),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F, FontStyle.Bold)
            };

            // Add label for taskbar preview
            var userTaskbarPreviewLabel = new Label
            {
                Text = "Taskbar Preview:",
                Location = new Point(20, 55),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            pbUserTaskbarPreview = new PictureBox
            {
                Location = new Point(20, 70),
                Size = new Size(grpCustomThemes.Width - -160, 25),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = SystemColors.Control
            };

            grpCustomThemes.Controls.Add(customThemeLabel);
            grpCustomThemes.Controls.Add(customThemeComboBox);
            grpCustomThemes.Controls.Add(btnApplyCustomTheme);
            grpCustomThemes.Controls.Add(userTaskbarPreviewLabel);
            grpCustomThemes.Controls.Add(pbUserTaskbarPreview);

            // ============================================
            // Separator - Because we need visual breaks
            // ============================================
            Label separator = new Label
            {
                AutoSize = false,
                Height = 2,
                Width = tabPageUserThemes.ClientSize.Width - 40,
                Location = new Point(10, grpCustomThemes.Bottom + 15),
                BorderStyle = BorderStyle.Fixed3D,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // ============================================
            // Theme Creation Section - Where users become gods
            // ============================================
            GroupBox grpCreateTheme = new GroupBox
            {
                Text = "Create New Theme",
                Location = new Point(15, separator.Bottom + 15),
                Size = new Size(tabPageUserThemes.ClientSize.Width - 30, 380),
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // Taskbar color controls - Because black is boring
            btnSetTaskbarColor = new Button
            {
                Text = "Set Taskbar Color",
                Location = new Point(20, 30),
                Size = new Size(150, 30),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F)
            };
            btnSetTaskbarColor.Click += BtnSetTaskbarColor_Click;

            // Transparency controls - For when you want to see your mistakes
            var lblTransparency = new Label
            {
                Text = "Taskbar Transparency:",
                Location = new Point(20, 80),
                AutoSize = true,
                Font = new Font("Tahoma", 9F),
                BackColor = Color.Transparent
            };

            tbTransparency = new TrackBar
            {
                Location = new Point(20, 100),
                Width = 200,
                Minimum = 0,
                Maximum = 255,
                Value = 255,
                TickFrequency = 32
            };

            lblTransparencyValue = new Label
            {
                Text = "255",
                Location = new Point(230, 100),
                AutoSize = true,
                Font = new Font("Tahoma", 9F),
                BackColor = Color.Transparent
            };

            // Wallpaper controls - The main attraction
            btnSetWallpaper = new Button
            {
                Text = "Set Wallpaper",
                Location = new Point(20, 160),
                Size = new Size(150, 30),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F)
            };
            btnSetWallpaper.Click += BtnSetWallpaper_Click;

            // Label for wallpaper preview - Because seeing is believing
            var userWallpaperPreviewLabel = new Label
            {
                Text = "Wallpaper Preview:",
                Location = new Point(180, 20),
                AutoSize = true,
                Font = new Font("Tahoma", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
                BackColor = Color.Transparent
            };

            pbUserWallpaperPreview = new PictureBox
            {
                Location = new Point(180, 35),
                Size = new Size(250, 150),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = SystemColors.Control
            };

            // Save button - For when you want to preserve your masterpiece
            btnSaveTheme = new Button
            {
                Text = "Save As New Theme",
                Location = new Point(340, 190),
                Size = new Size(150, 20),
                FlatStyle = FlatStyle.System,
                Font = new Font("Tahoma", 8.25F, FontStyle.Bold)
            };
            btnSaveTheme.Click += BtnSaveTheme_Click;

            // Add controls to group box - Like herding cats
            grpCreateTheme.Controls.Add(btnSetTaskbarColor);
            grpCreateTheme.Controls.Add(lblTransparency);
            grpCreateTheme.Controls.Add(tbTransparency);
            grpCreateTheme.Controls.Add(lblTransparencyValue);
            grpCreateTheme.Controls.Add(btnSetWallpaper);
            grpCreateTheme.Controls.Add(userWallpaperPreviewLabel);
            grpCreateTheme.Controls.Add(pbUserWallpaperPreview);
            grpCreateTheme.Controls.Add(btnSaveTheme);

            // Add to tab page - Finally!
            tabPageUserThemes.Controls.Add(grpCustomThemes);
            tabPageUserThemes.Controls.Add(separator);
            tabPageUserThemes.Controls.Add(grpCreateTheme);

            // Event handlers - Where the magic happens
            btnApplyCustomTheme.Click += BtnApplyCustomTheme_Click;
            tbTransparency.Scroll += (s, e) =>
            {
                userAlpha = tbTransparency.Value;
                lblTransparencyValue.Text = userAlpha.ToString();
                UpdateUserPreview();
            };

            // Add resize handler for taskbar preview
            pbUserTaskbarPreview.Resize += (s, e) => UpdateUserPreview();

            // Add resize handlers for responsive UI - Because we care (sort of)
            grpCustomThemes.Resize += (s, e) => {
                customThemeComboBox.Width = grpCustomThemes.Width - 300;
                btnApplyCustomTheme.Left = grpCustomThemes.Width - 150;
            };

            grpCreateTheme.Resize += (s, e) => {
                pbUserWallpaperPreview.Left = grpCreateTheme.Width - 270;
                pbUserWallpaperPreview.Width = 250;
                userWallpaperPreviewLabel.Left = pbUserWallpaperPreview.Left;
            };
        }

        // OnLoad - Where we actually do some work
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadThemes();
            LoadCustomThemes();
            UpdateCurrentThemeInfo();
            LoadStartMenuSettings();  // Renamed from LoadOpenShellSettings
        }

        // OnShown - Because we like to show off
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.BeginInvoke((Action)(() => {
                if (mainTabControl.SelectedTab == tabPageUserThemes)
                {
                    UpdateUserPreview();
                }
            }));
        }

        // LoadStartMenuSettings - Because we renamed everything
        private void LoadStartMenuSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(OpenShellKey))
                {
                    if (key != null)
                    {
                        // Get current style
                        string menuStyle = key.GetValue("MenuStyle", "Win7").ToString();
                        currentOpenShellStyle = menuStyle;

                        // Set combo box selection
                        openShellStyleComboBox.SelectedIndex = menuStyle switch
                        {
                            "Win7" => 0,
                            "Classic2" => 1,  // Windows XP Style
                            _ => 0
                        };

                        // Get current skin based on style
                        string skinValueName = menuStyle switch
                        {
                            "Win7" => "SkinW7",
                            "Classic2" => "SkinC2",  // Only for Windows XP Style
                            _ => "SkinW7"
                        };
                        currentOpenShellSkin = key.GetValue(skinValueName, "").ToString();

                        lblOpenShellStatus.Text = $"Current style: {menuStyle}, Current skin: {currentOpenShellSkin}";
                    }
                    else
                    {
                        lblOpenShellStatus.Text = "Start Menu registry key not found";
                    }
                }

                // Populate skins for current style
                PopulateOpenShellSkins();
            }
            catch (Exception ex)
            {
                lblOpenShellStatus.Text = "Error loading Start Menu settings: " + ex.Message;
            }
        }

        // LoadThemes - Where we find all those pretty themes
        private void LoadThemes()
        {
            themeComboBox.Items.Clear();
            statusLabel.Text = "Scanning for themes...";

            try
            {
                if (!Directory.Exists(ThemesPath))
                {
                    Directory.CreateDirectory(ThemesPath);
                    statusLabel.Text = "Created themes directory";
                }

                var themeFiles = Directory.GetFiles(ThemesPath, "*.theme")
                    .Where(f => f.Contains("Longhorn", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .ToArray();

                if (themeFiles.Length == 0)
                {
                    statusLabel.Text = "No Longhorn themes found! Place .theme files in:";
                    return;
                }

                themeComboBox.Items.AddRange(themeFiles);
                themeComboBox.SelectedIndex = 0;
                statusLabel.Text = $"Found {themeFiles.Length} Longhorn themes";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error loading themes: {ex.Message}";
            }
        }

        // LoadCustomThemes - For the special snowflakes
        private void LoadCustomThemes()
        {
            customThemeComboBox.Items.Clear();

            try
            {
                if (!Directory.Exists(CustomThemesPath))
                {
                    Directory.CreateDirectory(CustomThemesPath);
                }

                var customThemes = Directory.GetFiles(CustomThemesPath, "*.theme")
                    .Select(Path.GetFileName)
                    .ToArray();

                if (customThemes.Length > 0)
                {
                    customThemeComboBox.Items.AddRange(customThemes);
                    customThemeComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading custom themes: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // themeComboBox_SelectedIndexChanged - Because choices matter
        private void themeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateThemePreview();
        }

        // UpdateThemePreview - Show them what they're getting into
        private void UpdateThemePreview()
        {
            if (wallpaperPreview.Image != null)
            {
                wallpaperPreview.Image.Dispose();
                wallpaperPreview.Image = null;
            }

            if (themeComboBox.SelectedItem == null)
            {
                themeDetailsLabel.Text = "No theme selected";
                UpdateTaskbarPreview(Color.Gray);
                return;
            }

            string themeFile = themeComboBox.SelectedItem.ToString();
            string fullPath = Path.Combine(ThemesPath, themeFile);

            if (!File.Exists(fullPath))
            {
                themeDetailsLabel.Text = "Theme file not found";
                UpdateTaskbarPreview(Color.Gray);
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(fullPath);
                string wallpaperPath = null;
                string themeName = Path.GetFileNameWithoutExtension(themeFile);
                string visualStyle = null;
                string colorScheme = "NormalColor";

                foreach (string line in lines)
                {
                    if (line.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase))
                    {
                        themeName = line.Substring("DisplayName=".Length).Trim();
                    }
                    else if (line.StartsWith("Wallpaper=", StringComparison.OrdinalIgnoreCase))
                    {
                        wallpaperPath = line.Substring("Wallpaper=".Length).Trim();
                    }
                    else if (line.StartsWith("ColorScheme=", StringComparison.OrdinalIgnoreCase))
                    {
                        colorScheme = line.Substring("ColorScheme=".Length).Trim();
                    }
                }

                visualStyle = GetVisualStylePathFromThemeFile(fullPath);
                StringBuilder details = new StringBuilder();
                details.AppendLine($"Theme: {themeName}");
                details.AppendLine($"File: {themeFile}");

                if (visualStyle != null)
                {
                    details.AppendLine($"Visual Style: {Path.GetFileName(visualStyle)}");
                }
                else
                {
                    details.AppendLine("WARNING: Visual Style not found in theme file!");
                }

                details.AppendLine($"Color Scheme: {colorScheme}");
                bool wallpaperLoaded = false;
                if (!string.IsNullOrEmpty(wallpaperPath))
                {
                    wallpaperPath = Environment.ExpandEnvironmentVariables(wallpaperPath);
                    if (!Path.IsPathRooted(wallpaperPath))
                    {
                        string themeDir = Path.GetDirectoryName(fullPath);
                        string relativePath = Path.Combine(themeDir, wallpaperPath);

                        if (File.Exists(relativePath))
                        {
                            wallpaperPath = relativePath;
                        }
                        else
                        {
                            string sysPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                "Web",
                                "Wallpaper",
                                Path.GetFileName(wallpaperPath));

                            if (File.Exists(sysPath))
                            {
                                wallpaperPath = sysPath;
                            }
                            else
                            {
                                string themeRelative = Path.Combine(themeDir, "Wallpaper", Path.GetFileName(wallpaperPath));
                                if (File.Exists(themeRelative))
                                {
                                    wallpaperPath = themeRelative;
                                }
                            }
                        }
                    }

                    if (File.Exists(wallpaperPath))
                    {
                        try
                        {
                            using (var img = Image.FromFile(wallpaperPath))
                            {
                                wallpaperPreview.Image = new Bitmap(img);
                            }
                            details.AppendLine($"Wallpaper: {Path.GetFileName(wallpaperPath)}");
                            wallpaperLoaded = true;
                        }
                        catch (Exception ex)
                        {
                            details.AppendLine($"Wallpaper Error: {ex.Message}");
                        }
                    }
                    else
                    {
                        details.AppendLine($"Wallpaper not found: {wallpaperPath}");
                    }
                }
                else
                {
                    details.AppendLine("Wallpaper: Not specified");
                }

                themeDetailsLabel.Text = details.ToString();
                if (themeFile.Contains("Aero", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateTaskbarPreview(Color.FromArgb(0x44, 0x33, 0x44));
                }
                else
                {
                    UpdateTaskbarPreview(Color.Black);
                }
            }
            catch (Exception ex)
            {
                themeDetailsLabel.Text = $"Error loading preview: {ex.Message}";
                UpdateTaskbarPreview(Color.Gray);
            }
        }

        // GetVisualStylePathFromThemeFile - Because we need to know where the good stuff is
        private string GetVisualStylePathFromThemeFile(string themePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(themePath);
                bool inVisualStylesSection = false;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        inVisualStylesSection = trimmedLine.Equals("[VisualStyles]", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (inVisualStylesSection && trimmedLine.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmedLine.Substring("Path=".Length).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        // UpdateTaskbarPreview - Because the taskbar needs love too
        private void UpdateTaskbarPreview(Color color)
        {
            Bitmap bmp = new Bitmap(taskbarPreview.Width, taskbarPreview.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(color);
                Rectangle startButton = new Rectangle(5, 5, 60, 20);
                g.FillRectangle(Brushes.DarkGray, startButton);
                g.DrawString("Start", new Font("Tahoma", 8, FontStyle.Bold), Brushes.White, startButton.X + 5, startButton.Y + 4);
                string time = DateTime.Now.ToString("h:mm");
                SizeF timeSize = g.MeasureString(time, new Font("Tahoma", 8));
                float timeX = bmp.Width - timeSize.Width - 10;
                float timeY = (bmp.Height - timeSize.Height) / 2;
                g.DrawString(time, new Font("Tahoma", 8), Brushes.White, timeX, timeY);
            }

            if (taskbarPreview.Image != null)
            {
                var old = taskbarPreview.Image;
                taskbarPreview.Image = null;
                old.Dispose();
            }
            taskbarPreview.Image = bmp;
        }

        // UpdateCurrentThemeInfo - For those who forget what they're using
        private void UpdateCurrentThemeInfo()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartIsBackKey))
                {
                    if (key != null)
                    {
                        string themeFile = key.GetValue("ThemeFile", "") as string;
                        if (!string.IsNullOrEmpty(themeFile))
                        {
                            currentThemeLabel.Text = $"Theme: {themeFile}";
                        }
                    }
                }
            }
            catch { }
        }

        // previewButton_Click - Because people like sneak peeks
        private void previewButton_Click(object sender, EventArgs e)
        {
            if (themeComboBox.SelectedItem == null) return;
            string themeFile = themeComboBox.SelectedItem.ToString();
            string color, opacityText;
            int opacity;
            if (themeFile.Contains("Aero", StringComparison.OrdinalIgnoreCase))
            {
                color = "#443344 (Purplish Blue)";
                opacity = 112;
                opacityText = "44% opaque";
            }
            else
            {
                color = "#000000 (Pure Black)";
                opacity = 255;
                opacityText = "100% opaque";
            }
            string info = $"Theme: {themeFile}\n" +
                          $"Taskbar Color: {color}\n" +
                          $"Opacity: {opacity}/255 ({opacityText})\n" +
                          $"Blur Effect: Disabled";
            MessageBox.Show(info, "Theme Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // applyButton_Click - Where the magic happens (or crashes)
        private void applyButton_Click(object sender, EventArgs e)
        {
            if (themeComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a theme first.",
                    "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string themeFile = themeComboBox.SelectedItem.ToString();
            string fullPath = Path.Combine(ThemesPath, themeFile);

            if (!File.Exists(fullPath))
            {
                MessageBox.Show($"Theme file not found:\n{fullPath}",
                    "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!IsValidThemeFile(fullPath))
            {
                MessageBox.Show("Invalid theme file structure. Missing required sections.",
                    "Invalid Theme", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                statusLabel.Text = $"Applying {themeFile}...";
                statusLabel.ForeColor = Color.Blue;
                Application.DoEvents();

                DisableErrorReporting();
                ApplyThemeSilently(fullPath);

                statusLabel.Text = "Configuring taskbar...";
                Application.DoEvents();
                ConfigureStartIsBack(themeFile);

                statusLabel.Text = "Restarting Explorer...";
                Application.DoEvents();
                RestartExplorer();

                UpdateCurrentThemeInfo();
                statusLabel.Text = $"Successfully applied {themeFile}";
                statusLabel.ForeColor = Color.Green;
                MessageBox.Show($"The '{themeFile}' theme has been applied successfully.",
                    "Theme Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
                statusLabel.ForeColor = Color.Red;

                string logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "theme_error.log");
                File.WriteAllText(logPath,
                    $"Theme: {themeFile}\n" +
                    $"Path: {fullPath}\n" +
                    $"Error: {ex}\n" +
                    $"Stack Trace:\n{ex.StackTrace}");

                MessageBox.Show($"Error applying theme:\n{ex.Message}\n\nDetailed error log created at:\n{logPath}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                new Thread(() =>
                {
                    Thread.Sleep(5000);
                    EnableErrorReporting();
                }).Start();
            }
        }

        // DisableErrorReporting - Because we don't need no stinking error reports
        private void DisableErrorReporting()
        {
            try
            {
                ServiceController werService = new ServiceController("WerSvc");
                if (werService.Status == ServiceControllerStatus.Running)
                {
                    statusLabel.Text += " (Stopping error service)";
                    werService.Stop();
                    werService.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disabling WER service: {ex.Message}");
            }
        }

        // EnableErrorReporting - Okay, maybe we do need them sometimes
        private void EnableErrorReporting()
        {
            try
            {
                ServiceController werService = new ServiceController("WerSvc");
                if (werService.Status != ServiceControllerStatus.Running)
                {
                    statusLabel.Text += " (Restarting error service)";
                    werService.Start();
                    werService.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enabling WER service: {ex.Message}");
            }
        }

        // ConfigureStartIsBack - Because the start menu needs love too
        private void ConfigureStartIsBack(string themeFileName)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartIsBackKey))
            {
                if (themeFileName.Contains("Aero", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("TaskbarColor", 0x00443344, RegistryValueKind.DWord);
                    key.SetValue("TaskbarAlpha", 112, RegistryValueKind.DWord);
                }
                else if (themeFileName.Contains("Dark", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("TaskbarColor", 0x00000000, RegistryValueKind.DWord);
                    key.SetValue("TaskbarAlpha", 255, RegistryValueKind.DWord);
                }
                key.SetValue("TaskbarBlur", 0, RegistryValueKind.DWord);
                key.SetValue("ThemeFile", themeFileName, RegistryValueKind.String);
            }
        }

        // IsValidThemeFile - Because we don't trust users
        private bool IsValidThemeFile(string path)
        {
            try
            {
                string content = File.ReadAllText(path);
                return content.Contains("[VisualStyles]") &&
                       content.Contains("Path=");
            }
            catch
            {
                return false;
            }
        }

        // ApplyThemeSilently - Because we don't need no stinking dialogs
        private void ApplyThemeSilently(string themePath)
        {
            string tempPath = "";
            try
            {
                int oldMode;
                NativeMethods.SetThreadErrorMode(NativeMethods.SEM_FAILCRITICALERRORS | NativeMethods.SEM_NOGPFAULTERRORBOX, out oldMode);

                tempPath = Path.Combine(Path.GetTempPath(), "lh_" + Guid.NewGuid().ToString("N") + ".theme");

                if (File.Exists(tempPath))
                {
                    File.SetAttributes(tempPath, FileAttributes.Normal);
                    File.Delete(tempPath);
                }

                File.Copy(themePath, tempPath);

                int fixResult = NativeMethods.ThemeTool_SignatureFix(tempPath);
                if (fixResult != 0)
                    throw new Exception($"Signature fix failed with error: 0x{fixResult:X8}");

                ThemeCore.ApplyTheme(tempPath);

                new Thread(() => {
                    Thread.Sleep(10000);
                    NativeMethods.SetThreadErrorMode(oldMode, out _);
                }).Start();
            }
            catch (Exception ex)
            {
                throw new Exception($"Theme application failed: {ex.Message}", ex);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.SetAttributes(tempPath, FileAttributes.Normal);
                        File.Delete(tempPath);
                    }
                }
                catch { }
            }
        }

        // ==================================
        // USER THEME TAB FUNCTIONALITY
        // ==================================

        // BtnSetTaskbarColor_Click - Because black is boring
        private void BtnSetTaskbarColor_Click(object sender, EventArgs e)
        {
            if (colorDialog == null) colorDialog = new ColorDialog();
            if (colorDialog.ShowDialog() == DialogResult.OK)
            {
                userTaskbarColor = colorDialog.Color;
                UpdateUserPreview();
            }
        }

        // BtnSetWallpaper_Click - For the pretty pictures
        private void BtnSetWallpaper_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp"
            })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    userWallpaperPath = ofd.FileName;
                    UpdateUserWallpaperPreview();
                }
            }
        }

        // BtnApplyCustomTheme_Click - Because users like to customize
        private void BtnApplyCustomTheme_Click(object sender, EventArgs e)
        {
            if (customThemeComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a custom theme first.",
                    "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string themeFile = customThemeComboBox.SelectedItem.ToString();
            string fullPath = Path.Combine(CustomThemesPath, themeFile);

            if (!File.Exists(fullPath))
            {
                MessageBox.Show($"Custom theme file not found:\n{fullPath}",
                    "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!IsValidThemeFile(fullPath))
            {
                MessageBox.Show("Invalid custom theme file structure.",
                    "Invalid Theme", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                statusLabel.Text = $"Applying {themeFile}...";
                statusLabel.ForeColor = Color.Blue;
                Application.DoEvents();

                DisableErrorReporting();
                ApplyThemeSilently(fullPath);

                LoadCustomSettingsFromTheme(fullPath);

                statusLabel.Text = "Configuring taskbar...";
                Application.DoEvents();
                ConfigureTaskbarForCustomTheme();

                statusLabel.Text = "Restarting Explorer...";
                Application.DoEvents();
                RestartExplorer();

                UpdateCurrentThemeInfo();
                statusLabel.Text = $"Successfully applied {themeFile}";
                statusLabel.ForeColor = Color.Green;
                MessageBox.Show($"The '{themeFile}' theme has been applied successfully.",
                    "Theme Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Error: {ex.Message}";
                statusLabel.ForeColor = Color.Red;

                string logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "theme_error.log");
                File.WriteAllText(logPath,
                    $"Theme: {themeFile}\n" +
                    $"Path: {fullPath}\n" +
                    $"Error: {ex}\n" +
                    $"Stack Trace:\n{ex.StackTrace}");

                MessageBox.Show($"Error applying theme:\n{ex.Message}\n\nDetailed error log created at:\n{logPath}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                new Thread(() =>
                {
                    Thread.Sleep(5000);
                    EnableErrorReporting();
                }).Start();
            }
        }

        // LoadCustomSettingsFromTheme - Because we remember your mistakes
        private void LoadCustomSettingsFromTheme(string themePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(themePath);
                bool inCustomSection = false;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        inCustomSection = trimmedLine.Equals("[LonghornThemeManager]", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (inCustomSection)
                    {
                        if (trimmedLine.StartsWith("TaskbarColor="))
                        {
                            string value = trimmedLine.Substring("TaskbarColor=".Length).Trim();
                            if (uint.TryParse(value, out uint colorValue))
                            {
                                userTaskbarColor = Color.FromArgb(
                                    (int)(colorValue & 0xFF),
                                    (int)((colorValue >> 8) & 0xFF),
                                    (int)((colorValue >> 16) & 0xFF));
                            }
                        }
                        else if (trimmedLine.StartsWith("TaskbarAlpha="))
                        {
                            string value = trimmedLine.Substring("TaskbarAlpha=".Length).Trim();
                            if (int.TryParse(value, out int alpha))
                            {
                                userAlpha = alpha;
                                tbTransparency.Value = alpha;
                                lblTransparencyValue.Text = alpha.ToString();
                            }
                        }
                    }
                }

                UpdateUserPreview();
            }
            catch { }
        }

        // ConfigureTaskbarForCustomTheme - Making your desktop pretty
        private void ConfigureTaskbarForCustomTheme()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartIsBackKey))
            {
                key.SetValue("TaskbarColor", ColorToUInt(userTaskbarColor), RegistryValueKind.DWord);
                key.SetValue("TaskbarAlpha", userAlpha, RegistryValueKind.DWord);
                key.SetValue("TaskbarBlur", 0, RegistryValueKind.DWord);
            }
        }

        // BtnSaveTheme_Click - For posterity
        private void BtnSaveTheme_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(userWallpaperPath))
            {
                MessageBox.Show("Please set a wallpaper before saving the theme",
                    "Wallpaper Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Theme Files (*.theme)|*.theme",
                InitialDirectory = CustomThemesPath,
                Title = "Save Custom Theme",
                FileName = "MyCustomTheme.theme"
            })
            {
                if (sfd.ShowDialog() != DialogResult.OK)
                    return;

                try
                {
                    string themePath = sfd.FileName;

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("[Theme]");
                    sb.AppendLine($"DisplayName={Path.GetFileNameWithoutExtension(themePath)}");
                    sb.AppendLine();
                    sb.AppendLine("[Control Panel\\Desktop]");
                    sb.AppendLine($"Wallpaper={userWallpaperPath}");
                    sb.AppendLine("WallpaperStyle=10");
                    sb.AppendLine();
                    sb.AppendLine("[LonghornThemeManager]");
                    sb.AppendLine($"TaskbarColor={ColorToUInt(userTaskbarColor)}");
                    sb.AppendLine($"TaskbarAlpha={userAlpha}");
                    sb.AppendLine();
                    sb.AppendLine("[VisualStyles]");
                    sb.AppendLine("Path=%SystemRoot%\\resources\\themes\\Longhorn\\Longhorn.msstyles");
                    sb.AppendLine("ColorStyle=NormalColor");
                    sb.AppendLine("Size=NormalSize");

                    File.WriteAllText(themePath, sb.ToString());

                    LoadCustomThemes();
                    customThemeComboBox.SelectedItem = Path.GetFileName(themePath);

                    MessageBox.Show("Custom theme saved successfully!", "Theme Saved",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving theme: {ex.Message}", "Save Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // UpdateUserPreview - Show them what they've created
        private void UpdateUserPreview()
        {
            if (pbUserTaskbarPreview == null ||
                pbUserTaskbarPreview.Width <= 0 ||
                pbUserTaskbarPreview.Height <= 0)
                return;

            try
            {
                int width = pbUserTaskbarPreview.Width;
                int height = pbUserTaskbarPreview.Height;

                // Create new bitmap for preview
                Bitmap bmp = new Bitmap(width, height);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // Get background color (use PictureBox's actual background)
                    Color bgColor = pbUserTaskbarPreview.BackColor;

                    // Calculate blended color (simulate transparency)
                    Color blendedColor = BlendColors(
                        Color.FromArgb(userAlpha, userTaskbarColor),
                        bgColor
                    );

                    // Fill with blended color
                    using (SolidBrush bgBrush = new SolidBrush(blendedColor))
                    {
                        g.FillRectangle(bgBrush, new Rectangle(0, 0, width, height));
                    }

                    // Draw start button
                    if (width > 70 && height > 20)
                    {
                        int startButtonHeight = 20;
                        Rectangle startButton = new Rectangle(5, (height - startButtonHeight) / 2, 60, startButtonHeight);
                        g.FillRectangle(Brushes.DarkGray, startButton);
                        g.DrawString("Start", new Font("Tahoma", 8, FontStyle.Bold),
                                     Brushes.White, startButton.X + 5, startButton.Y + 4);
                    }

                    // Draw clock
                    if (width > 100)
                    {
                        string time = DateTime.Now.ToString("h:mm");
                        using (var font = new Font("Tahoma", 8))
                        {
                            SizeF timeSize = g.MeasureString(time, font);
                            float timeX = width - timeSize.Width - 10;
                            float timeY = (height - timeSize.Height) / 2;
                            g.DrawString(time, font, Brushes.White, timeX, timeY);
                        }
                    }
                }

                // Update control with proper disposal
                if (pbUserTaskbarPreview.Image != null)
                {
                    var oldImage = pbUserTaskbarPreview.Image;
                    pbUserTaskbarPreview.Image = null;
                    oldImage.Dispose();
                }
                pbUserTaskbarPreview.Image = bmp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Preview error: {ex.Message}");
            }
        }

        // Helper function to blend colors with transparency
        private Color BlendColors(Color foreground, Color background)
        {
            double alpha = foreground.A / 255.0;
            int r = (int)((foreground.R * alpha) + (background.R * (1 - alpha)));
            int g = (int)((foreground.G * alpha) + (background.G * (1 - alpha)));
            int b = (int)((foreground.B * alpha) + (background.B * (1 - alpha)));
            return Color.FromArgb(r, g, b);
        }

        // UpdateUserWallpaperPreview - Show off that wallpaper
        private void UpdateUserWallpaperPreview()
        {
            if (string.IsNullOrEmpty(userWallpaperPath) || !File.Exists(userWallpaperPath)) return;

            try
            {
                if (pbUserWallpaperPreview.Image != null)
                {
                    var old = pbUserWallpaperPreview.Image;
                    pbUserWallpaperPreview.Image = null;
                    old.Dispose();
                }

                using (var img = Image.FromFile(userWallpaperPath))
                {
                    pbUserWallpaperPreview.Image = new Bitmap(img);
                }
            }
            catch
            {
                MessageBox.Show("Error loading selected wallpaper.");
            }
        }

        // ColorToUInt - Because computers like numbers, not pretty colors
        private uint ColorToUInt(Color c)
        {
            return (uint)((c.R) | (c.G << 8) | (c.B << 16));
        }
    }
}