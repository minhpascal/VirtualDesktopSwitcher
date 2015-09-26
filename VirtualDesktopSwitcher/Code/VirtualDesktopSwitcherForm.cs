﻿using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsInput;
using WindowsInput.Native;
using System.Drawing;
using System.Text;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VirtualDesktopSwitcher.Code;

namespace VirtualDesktopSwitcher
{
    public partial class VirtualDesktopSwitcherForm : Form
    {
        #region WinAPIFunctions
        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern int SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hInstance, int threadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(int idHook);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern int CallNextHookEx(int idHook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder title, int size);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpszClass, string lpszWindow);

        #endregion

        #region Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x; // LONG
            public int y; // LONG

            public POINT(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public int mouseData; // DWORD
            public int flags; // DWORD
            public int time; // DWORD
            public IntPtr dwExtraInfo; // ULONG_PTR
        }
        #endregion

        public bool hideOnStartup { get; private set; }
        public delegate int HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static VirtualDesktopSwitcherForm formInstance;
        private static IKeyboardSimulator keyboardSimulator;
        private static List<Rectangle> rectangles;
        private static IntPtr StartMenu;
        private static bool desktopScroll;
        private static bool taskViewScroll;
        private static int hHook = 0;

        private HookProc mouseHookProcedure; // Need to keep a reference to hookproc or otherwise it will be GC:ed.
        private List<Form> forms;
        private dynamic jsonConfig;
        private TreeNode clickedNode;

        private const string CONFIG_FILENAME = "config.json";
        private const string SHORTCUT_FILENAME = "\\VirtualDesktopSwitcher.lnk";

        public VirtualDesktopSwitcherForm()
        {
            InitializeComponent();

            formInstance = this;
            keyboardSimulator = (new InputSimulator()).Keyboard;
            rectangles = new List<Rectangle>();
            forms = new List<Form>();

            ReadConfig();
            CheckForStartupShortcut();
            AttachHook();
            FindStartMenu();
        }

        #region Event handlers
        private void VirtualDesktopSwitcherForm_VisibleChanged(object sender, EventArgs e)
        {
            if (Visible) ShowRectangles();
            else HideRectangles();
        }

        private void desktopScrollCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            desktopScroll = desktopScrollCheckbox.Checked;
            jsonConfig.desktopScroll = desktopScroll;
            UpdateConfigJsonFile();
        }

        private void taskViewButtonScrollCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            taskViewScroll = taskViewButtonScrollCheckbox.Checked;
            jsonConfig.taskViewScroll = taskViewScroll;
            UpdateConfigJsonFile();
        }

        private void hideOnStartupCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            jsonConfig.hideOnStartup = hideOnStartupCheckbox.Checked;
            UpdateConfigJsonFile();
        }

        private void ToggleVisibilityWithMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Visible = !Visible;
                TopMost = Visible;
            }
        }

        private void exitMenuItem_Click(object sender, EventArgs e)
        {
            notifyIcon.Visible = false;
            Application.Exit();
        }

        private void loadOnWindowsStartupCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (loadOnWindowsStartupCheckbox.Checked)
            {
                IWshRuntimeLibrary.WshShell wsh = new IWshRuntimeLibrary.WshShell();
                IWshRuntimeLibrary.IWshShortcut shortcut = wsh.CreateShortcut(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup) + SHORTCUT_FILENAME)
                    as IWshRuntimeLibrary.IWshShortcut;
                shortcut.Arguments = "";
                shortcut.TargetPath = System.Reflection.Assembly.GetEntryAssembly().Location;
                shortcut.Description = "VisualDesktopSwitcher";
                shortcut.WorkingDirectory = Environment.CurrentDirectory;
                shortcut.Save();
            }
            else
            {
                var path = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                File.Delete(path + SHORTCUT_FILENAME);
            }
        }

        private void VirtualDesktopSwitcherForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            notifyIcon.Visible = false;
            Application.Exit();
        }

        private void rectanglesTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Level == 2)
            {
                e.Node.TreeView.LabelEdit = true;
                e.Node.BeginEdit();
            }
        }

        private void rectanglesTreeView_AfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            int rectangleIndex = e.Node.Parent.Parent.Index;
            string propertyName = e.Node.Parent.Text;
            int value;

            if (int.TryParse(e.Label, out value))
            {
                rectangles[rectangleIndex].Set(propertyName, value);
                jsonConfig.rectangles[rectangleIndex][propertyName] = value;
                UpdateConfigJsonFile();
                HideRectangles();
                ShowRectangles();
            }
            else
            {
                e.CancelEdit = true;
            }
            e.Node.TreeView.LabelEdit = false;
        }

        private void addRectangleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var node = rectanglesTreeView.Nodes.Add("rectangle " + (rectanglesTreeView.Nodes.Count + 1));
            rectanglesTreeView.SelectedNode = node;

            Action<string, string> addSubNode = (label, value) =>
            {
                var subnode = node.Nodes.Add(label);
                subnode.Nodes.Add(value);
                subnode.ExpandAll();
            };

            addSubNode("x", "0");
            addSubNode("y", "0");
            addSubNode("width", "50");
            addSubNode("height", "40");

            rectangles.Add(new Rectangle(0, 0, 50, 40));
            var jObject = JsonConvert.DeserializeObject(@"{""x"": 0, ""y"": 0, ""width"": 50, ""height"": 40}");
            jsonConfig.rectangles.Add(jObject);
            HideRectangles();
            ShowRectangles();
            UpdateConfigJsonFile();
        }

        private void removeRectangleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var index = clickedNode.Index;
            clickedNode.Remove();
            rectangles.RemoveAt(index);
            jsonConfig.rectangles.RemoveAt(index);
            HideRectangles();
            ShowRectangles();
            UpdateConfigJsonFile();
        }

        private void rectanglesTreeView_MouseDown(object sender, MouseEventArgs e)
        {
            var node = rectanglesTreeView.GetNodeAt(e.Location);
            if (node != null && node.Level == 0)
            {
                rectanglesTreeView.SelectedNode = node;
                clickedNode = node;
                treeViewRightClickMenuRemove.Items[0].Text = "Remove rectangle " + (node.Index + 1);
                rectanglesTreeView.ContextMenuStrip = treeViewRightClickMenuRemove;
            }
            else
            {
                rectanglesTreeView.ContextMenuStrip = treeViewRightClickMenuAdd;
            }
        }

        private void ToggleRectangles(object sender = null, EventArgs e = null)
        {
            rectanglesTreeView.Visible ^= true;
            Height += (rectanglesTreeView.Visible ? 1 : -1) * rectanglesTreeView.Height;
            advancedLabel.Text = (rectanglesTreeView.Visible ? "-" : "+") + advancedLabel.Text.Substring(1);
        }

        private void VirtualDesktopSwitcherForm_Shown(object sender, EventArgs e)
        {
            ToggleRectangles();
        }
        #endregion

        private void FindStartMenu()
        {
            StartMenu = FindWindow("Shell_TrayWnd", null);
            if (StartMenu == IntPtr.Zero)
            {
                MessageBox.Show("Failed to find start menu!");
            }
        }

        private void ReadConfig()
        {
            using (var streamReader = new StreamReader(CONFIG_FILENAME))
            {
                string json = streamReader.ReadToEnd();
                jsonConfig = JsonConvert.DeserializeObject(json);

                if (jsonConfig.rectangles != null)
                {
                    foreach (var jsonRectangle in jsonConfig.rectangles)
                    {
                        int x = jsonRectangle.x;
                        int y = jsonRectangle.y;
                        int width = jsonRectangle.width;
                        int height = jsonRectangle.height;

                        rectangles.Add(new Rectangle(x, y, width, height));

                        var node = rectanglesTreeView.Nodes.Add("rectangle " + (rectanglesTreeView.Nodes.Count + 1));

                        Action<string, int> addSubNode = (label, value) =>
                        {
                            var subnode = node.Nodes.Add(label);
                            var subsubnode = subnode.Nodes.Add(value.ToString());
                            subnode.ExpandAll();
                        };

                        addSubNode("x", x);
                        addSubNode("y", y);
                        addSubNode("width", width);
                        addSubNode("height", height);
                    }
                }

                desktopScroll = jsonConfig.desktopScroll ?? false;
                taskViewScroll = jsonConfig.taskViewScroll ?? false;
                hideOnStartup = jsonConfig.hideOnStartup ?? false;
            }

            Action<bool, CheckBox, EventHandler> setChecked = (boolValue, checkBox, eventHandler) =>
            {
                checkBox.CheckedChanged -= eventHandler;
                checkBox.Checked = boolValue;
                checkBox.CheckedChanged += eventHandler;
            };

            setChecked(desktopScroll, desktopScrollCheckbox, desktopScrollCheckbox_CheckedChanged);
            setChecked(taskViewScroll, taskViewButtonScrollCheckbox, taskViewButtonScrollCheckbox_CheckedChanged);
            setChecked(hideOnStartup, hideOnStartupCheckbox, hideOnStartupCheckbox_CheckedChanged);
        }

        private void CheckForStartupShortcut()
        {
            if (File.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Startup) + SHORTCUT_FILENAME))
            {
                IWshRuntimeLibrary.WshShell wsh = new IWshRuntimeLibrary.WshShell();
                IWshRuntimeLibrary.IWshShortcut shortcut = wsh.CreateShortcut(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup) + SHORTCUT_FILENAME)
                    as IWshRuntimeLibrary.IWshShortcut;
                if (shortcut.TargetPath.ToLower() == System.Reflection.Assembly.GetEntryAssembly().Location.ToLower())
                {
                    loadOnWindowsStartupCheckbox.Checked = true;
                }
                else
                {
                    var path = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    File.Delete(path + SHORTCUT_FILENAME);
                    loadOnWindowsStartupCheckbox.Checked = false;
                }
            }
            else
            {
                loadOnWindowsStartupCheckbox.Checked = false;
            }
        }
        
        public void ExposeWndProc(ref Message m)
        {
            WndProc(ref m);
        }

        protected override void WndProc(ref Message m)
        {
            // Disable maximize and minimize system commands.
            if (m.Msg == WinApi.WM_SYSCOMMAND)
            {
                int wParam = m.WParam.ToInt32();
                if (wParam == WinApi.SC_MAXIMIZE || wParam == WinApi.SC_MINIMIZE)
                {
                    return;
                }
            }
            // Enable dragging of window by clicking in the form.
            else if (m.Msg == WinApi.WM_NCHITTEST)
            {
                base.WndProc(ref m);
                m.Result = (IntPtr)(WinApi.HT_CAPTION);
                return;
            }

            base.WndProc(ref m);
        }

        private void AttachHook()
        {
            if (hHook == 0)
            {
                mouseHookProcedure = new HookProc(LowLevelMouseProc);
                hHook = SetWindowsHookEx(WinApi.WH_MOUSE_LL, mouseHookProcedure, IntPtr.Zero, 0);

                // If the SetWindowsHookEx function fails.
                if (hHook == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    MessageBox.Show("SetWindowsHookEx Failed " + error);
                    return;
                }
            }
            else
            {
                MessageBox.Show("SetWindowsHookEx Failed - hHook was not null");
            }
        }

        private void DetachHook()
        {
            bool ret = UnhookWindowsHookEx(hHook);

            // If the UnhookWindowsHookEx function fails.
            if (ret == false)
            {
                MessageBox.Show("UnhookWindowsHookEx Failed");
                return;
            }
            hHook = 0;
        }

        private static void CtrlWinKey(VirtualKeyCode virtualKeyCode)
        {
            keyboardSimulator.KeyDown(VirtualKeyCode.LCONTROL);
            keyboardSimulator.KeyDown(VirtualKeyCode.LWIN);

            keyboardSimulator.KeyPress(virtualKeyCode);

            keyboardSimulator.KeyUp(VirtualKeyCode.LWIN);
            keyboardSimulator.KeyUp(VirtualKeyCode.LCONTROL);
        }

        private static bool CheckPoint(POINT point)
        {
            foreach (var rectangle in rectangles)
            {
                if (point.x > rectangle.Left && point.x < rectangle.Right &&
                    point.y > rectangle.Top && point.y < rectangle.Bottom)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetTitleFromWindowUnderPoint(POINT point)
        {
            var title = new StringBuilder(10);
            GetWindowText(WindowFromPoint(point), title, 11);
            return title.ToString();
        }

        private static bool IsScrollPoint(POINT point)
        {
            var title = GetTitleFromWindowUnderPoint(point);

            if ((rectangles.Count > 0 && CheckPoint(point)) ||
                (desktopScroll && title == "FolderView") ||
                (taskViewScroll && title == "Task View"))
            {
                return true;
            }
            return false;
        }
        
        private static bool IsVirtualBoxInForeground()
        {
            var foregroundWindow = GetForegroundWindow();
            var className = new StringBuilder(7);
            var title = new StringBuilder(255);
            GetClassName(foregroundWindow, className, 8);
            GetWindowText(foregroundWindow, title, 256);
            return className.ToString() == "QWidget" && title.ToString().EndsWith("VirtualBox");
        }

        public static int LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return CallNextHookEx(hHook, nCode, wParam, lParam);
            }

            if (wParam.ToInt32() == WinApi.WM_MOUSEWHEEL)
            {
                var msllHookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                if (IsScrollPoint(msllHookStruct.pt))
                {
                    int highOrder = msllHookStruct.mouseData >> 16;

                    if (IsVirtualBoxInForeground())
                    {
                        formInstance.Opacity = 0;
                        formInstance.Show();
                        formInstance.Activate();
                        SetForegroundWindow(StartMenu);
                        formInstance.Hide();
                        formInstance.Opacity = 1;
                    }

                    if (highOrder > 0)
                    {
                        CtrlWinKey(VirtualKeyCode.LEFT);
                    }
                    else
                    {
                        CtrlWinKey(VirtualKeyCode.RIGHT);
                    }
                }
            }

            if (formInstance.Visible)
            {
                var point = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
                formInstance.formTitle.Text = string.Format("{0} {1}", point.x, point.y);
                formInstance.BackColor = IsScrollPoint(point) ? Color.Yellow : SystemColors.Control;
            }

            return CallNextHookEx(hHook, nCode, wParam, lParam);
        }



        private void ShowRectangles()
        {
            foreach (var rectangle in rectangles)
            {
                var form = new Form
                {
                    Parent = null,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(rectangle.X, rectangle.Y),
                    MinimumSize = new Size(rectangle.Width, rectangle.Height),
                    Size = new Size(rectangle.Width, rectangle.Height),
                    TopMost = true,
                    BackColor = Color.Yellow,
                    ShowInTaskbar = false
                };
                form.Show();
                forms.Add(form);
            }
        }

        private void HideRectangles()
        {
            foreach (var form in forms)
            {
                form.Close();
            }
            forms.Clear();
        }

        private void UpdateConfigJsonFile()
        {
            File.WriteAllText(CONFIG_FILENAME, JsonConvert.SerializeObject(jsonConfig, Formatting.Indented));
        }
    }
}