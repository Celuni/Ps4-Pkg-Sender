﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ps4_Pkg_Sender.Controls.Sorting;
using Ps4_Pkg_Sender.Exceptions;
using Ps4_Pkg_Sender.Extensions;
using Ps4_Pkg_Sender.Ps4;
using Ps4_Pkg_Sender.Utilities;
using Ps4_Pkg_Sender.WinApi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ps4_Pkg_Sender {
    public partial class MainForm : Form {

        bool connected = false;

        bool processing = false;

        IntPtr listViewHwnd = IntPtr.Zero;
        List<QueueItem> ps4PkgList = new List<QueueItem>();

        ListViewColumnSorter listViewColumnSorter;

        Json.Settings Settings = new Json.Settings();

        BackgroundWorker queueBackgroundWorker;

        static Server server;

        struct Server {
            private static Random random = new Random();

            static readonly HashSet<int> NodeJsProcessSet = new HashSet<int>();

            int currentProcessPid;

            public string PS4IP { get; set; }
            public string ServerIp { get; set; }
            public static int ServerPort { get; set; } = 8080;

            public bool IsRunning { get; internal set; }

            private bool IsPortOpen(int port) {
                IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
                TcpConnectionInformation[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpConnections();
                foreach (TcpConnectionInformation tcpi in tcpConnInfoArray) {
                    if (tcpi.LocalEndPoint.Port == port) {
                        return false;
                    }
                }
                return true;
            }

            private int GetRandomPort() {
                return random.Next(29170, 29998);
            }

            private int GetNextBestPort() {
                if (!IsPortOpen(ServerPort)) {
                    ServerPort = GetRandomPort();
                }
                return ServerPort;
            }

            public void StartServer(PkgInfo info) {

                foreach(var proc in NodeJSUtil.GetNodeProcesses()) {
                    NodeJsProcessSet.Add(proc.Id);
                }

                Logger.WriteLine("::StartServer - Starting server in directory " + Path.GetDirectoryName(info.FilePath),Logger.Type.StandardOutput);
                var cmdProcess = new Process();
                var path = Path.GetDirectoryName(info.FilePath);
                cmdProcess.StartInfo.FileName = "cmd.exe";
                cmdProcess.StartInfo.Arguments = $"/C http-server \"{path}\" -p {GetNextBestPort()}";
                cmdProcess.StartInfo.UseShellExecute = false;
                cmdProcess.StartInfo.CreateNoWindow = true;
                cmdProcess.StartInfo.RedirectStandardOutput = true;
                cmdProcess.StartInfo.RedirectStandardError = true;
                if (Directory.GetLogicalDrives().Contains(path)) {
                    cmdProcess.StartInfo.Arguments = cmdProcess.StartInfo.Arguments.Replace(@"\", @"\\");
                }
                cmdProcess.Start();

                //Assumption will be that it should always be found
                currentProcessPid = 0;
                while (currentProcessPid == 0) {
                    try {
                        currentProcessPid = NodeJSUtil.GetNodeProcesses()
                                                      .ToList()
                                                      .Find(proc => !NodeJsProcessSet.Contains(proc.Id))
                                                      .Id;
                    } catch (Exception) {

                    }
                    System.Threading.Thread.Sleep(100);
                }


                var temp = cmdProcess;
                System.Threading.Tasks.Task.Run(() => {
                    while (temp.Handle == IntPtr.Zero) {
                        System.Threading.Thread.Sleep(100);
                    }
                    var stdout = "";
                    while (stdout != null) {
                        stdout = temp.StandardOutput.ReadLine();
                        if (stdout != null) {
                            Logger.WriteLine(stdout, Logger.Type.StandardOutput);
                        }
                    }
                });

                System.Threading.Tasks.Task.Run(() => {
                    while (temp.Handle == IntPtr.Zero) {
                        System.Threading.Thread.Sleep(100);
                    }
                    var stderr = "";
                    while (stderr != null) {
                        stderr = temp.StandardError.ReadLine();
                        if (stderr != null) {
                            if (stderr.Contains("EADDRINUSE")) {
                                server.IsRunning = false;
                                break;
                            }
                        }
                        Logger.WriteLine(stderr, Logger.Type.StandardOutput);
                    }
                });
                IsRunning = true;
            }

            private void KillProcess(int pid) {
                try {
                    var process = Process.GetProcessById(pid);
                    if (process != null && !process.HasExited) {
                        process.Kill();
                        process.WaitForExit();
                    }
                } catch {

                }
            }

            public void StopServer() {
                Logger.WriteLine("Killed Old Server Process",Logger.Type.DebugOutput);
                KillProcess(currentProcessPid);
                IsRunning = false;
            }

            public DataTrasmittedProgress GetInstallProgress(long taskID) {
                if (taskID == 0) return null;
                var response = HttpUtil.Post($"http://{PS4IP}:12800/api/get_task_progress", $"{{\"task_id\":{taskID}}}");
                Logger.WriteLine("::GetInstallProgress - " + response,Logger.Type.StandardOutput);
                var progress = JsonConvert.DeserializeObject<Json.Ps4Progress>(response);

                return new DataTrasmittedProgress(progress.LengthTotal,progress.TransferredTotal, progress.RestSecTotal);
            }


            private int RecoverTaskID(PkgInfo pkgInfo) {
                var url = $"http://{PS4IP}:12800/api/find_task";
                var json = $"{{\"content_id\":\"{pkgInfo.ContentID}\", \"sub_type\":{(int)pkgInfo.Type}}}";
                var response = HttpUtil.Post(url, json);
                Logger.WriteLine("::RecoverTaskID - " + response,Logger.Type.StandardOutput);
                if (response.Contains("task_id")) {
                    return int.Parse(JToken.Parse(response)["task_id"].ToString());
                }
                if (response.Contains("error_code")) {
                    throw new RPIErrorThrownException(long.Parse(JToken.Parse(response)["error_code"].ToString()));
                }
                return -1;
            }

            public bool TryRecoverTaskID(PkgInfo pkgInfo, out long id) {
                id = -1;
                id = RecoverTaskID(pkgInfo);
                if (id != -1) {
                    return true;
                }
                //Because it checks the title id instead of content id
                //this will return true if the game is installed
                //there is no way of knowing the patch is installed accurately
                //So we just return false and assume it's not.
                if (pkgInfo.Type == Enums.PkgType.Patch || pkgInfo.Type == Enums.PkgType.Additional_Content) {
                    return false;
                }
                var url = $"http://{PS4IP}:12800/api/is_exists";
                var json = $"{{\"title_id\":\"{pkgInfo.TitleID}\"}}";
                var response = HttpUtil.Post(url,json);
                if (response.Contains("task_id")) {
                    id = int.Parse(JToken.Parse(response)["task_id"].ToString());
                    return true;
                }
                Logger.WriteLine("::GetTaskID.AppExists - " + response,Logger.Type.StandardOutput);

                //App is already installed, usually a task ID comes with it but I'm not certain 
                //If this happens with DLC/Themes
                //Better safe than sorry
                return response.Contains("\"exists\": \"true\""); 
            }

            public bool InitiateInstall(PkgInfo pkgInfo, bool skipInstallCheck, out long id) {
                id = 0;
                if (!skipInstallCheck && TryRecoverTaskID(pkgInfo, out id)) {
                    if (id == -1) { //App is already installed and no id was returned
                        id = 0xAFFFFFF; //The flag we will use to determine if it is installed
                    }
                    return true;
                }
                var response = HttpUtil.Post($"http://{PS4IP}:12800/api/install",
                HttpUtil.GetInstallJson(pkgInfo.PkgFiles, ServerIp, ServerPort));
                //{ "status": "fail", "error_code": 0x80990004 }
                if (response.Contains("task_id")) {
                    id = long.Parse(JToken.Parse(response)["task_id"].ToString());
                    return true;
                }
                if (response.Contains("{ \"status\": \"fail\"")) {
                    throw new RPIErrorThrownException(long.Parse(JToken.Parse(response)["error_code"].ToString()));
                }
                return false;
            }

            public bool Uninstall(PkgInfo pkgInfo) {
                var url = $"http://{PS4IP}:12800/api/uninstall_";
                var json = "";
                switch (pkgInfo.Type) {
                    case Enums.PkgType.Game:
                        url += "game";
                        json = $"{{\"title_id\":\"{pkgInfo.TitleID}\"}}";
                        break;

                    case Enums.PkgType.Patch:
                    json = $"{{\"title_id\":\"{pkgInfo.TitleID}\"}}";
                    url += "patch";
                    break;

                    case Enums.PkgType.Additional_Content:
                        url += "ac";
                        json = $"{{\"content_id\":\"{pkgInfo.ContentID}\"}}";
                    break;

                    case Enums.PkgType.Addon_Theme:
                        url += "theme";
                        json = $"{{\"content_id\":\"{pkgInfo.ContentID}\"}}";
                    break;
                }

                var response = HttpUtil.Post(url,json);
                if (response.Contains("success")) {
                    return true;
                }
                return false;
            }
        }

        private Server GetServerDetails() {
            server = new Server();
            server.PS4IP = textBoxPS4IP.Text;
            server.ServerIp = comboBoxServerIP.SelectedItem.ToString();
            return server;
        }

        public MainForm() {
            InitializeComponent();
            listViewColumnSorter = new ListViewColumnSorter();
            listViewItemsQueue.ListViewItemSorter = listViewColumnSorter;
            queueBackgroundWorker = new BackgroundWorker();
            queueBackgroundWorker.WorkerReportsProgress = true;
            queueBackgroundWorker.WorkerSupportsCancellation = true;
            queueBackgroundWorker.DoWork += queueWorker_DoWork;
            queueBackgroundWorker.ProgressChanged += queueWorker_ProgressChanged;
        }

        private void SaveSettings() {
            var settings = new Json.Settings();
            settings.Ps4IP = textBoxPS4IP.Text;
            settings.ServerIP = comboBoxServerIP.SelectedItem.ToString();
            settings.RecursiveSearch = checkBoxRecursive.Checked;
            settings.ProgressCheckDelay = this.Settings.ProgressCheckDelay;
            File.WriteAllText("settings.json", JsonConvert.SerializeObject(settings, Formatting.Indented));
        }

        private void LoadSettings() {
            if (!File.Exists("settings.json")) {
                SaveSettings();
            }
            Settings = JsonConvert.DeserializeObject<Json.Settings>(File.ReadAllText("settings.json"));
            int serverIPIndex = this.comboBoxServerIP.FindStringExact(Settings.ServerIP);
            if (serverIPIndex != -1) {
                this.comboBoxServerIP.SelectedIndex = serverIPIndex;
            }
            this.textBoxPS4IP.Text = Settings.Ps4IP;
            checkBoxRecursive.Checked = Settings.RecursiveSearch;
        }

        private void CheckPreRequesites() {

            if (!NodeJSUtil.IsNodeJsInstalled()) {
                MessageBox.Show("Node JS is not installed, please install it.\nExiting...", "Node JS Not Found");
                Environment.Exit(1);
            }

            if (!NodeJSUtil.IsHttpServerInstalled()) {
                var dr = 
                    MessageBox.
                    Show("Node JS http-server is not installed, would you like to install it?", 
                    "Install http-server?",
                    MessageBoxButtons.YesNo);
                if(dr == DialogResult.Yes) {
                    System.Threading.Tasks.Task.Run(() =>
                        this.InvokeIfRequired(() =>
                        MessageBox.Show("Installing Http-server... Please wait for the popup", "Installing")
                    ));
                    if (NodeJSUtil.InstallHttpServer()) {
                        MessageBox.Show("Successfully installed!", "Success");
                    } else {
                        MessageBox.Show("Failed To Install... Please run \"npm install http-server\" in cmd (without the quotes)\nExiting...", "Failed");
                        Environment.Exit(1);
                    }
                } else {

                }
            }
        }

        private void MainForm_Load(object sender, EventArgs e) {
            CheckPreRequesites();
            this.listViewHwnd = listViewItemsQueue.Handle;
            PopulateValidIPs();
            this.listViewItemsQueue.Columns[listViewItemsQueue.Columns.Count - 1].Width = 254;
            this.listViewItemsQueue
                .GetType()
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(listViewItemsQueue, true);
            LoadSettings();
            this.labelCheckDelay.Text = $"Check Delay: {Settings.ProgressCheckDelay}s";
            this.toolTipInfo.SetToolTip(this.labelCheckDelay, 
                "The amount of seconds before checking the progress of the application (higher = less risk of crashing)"
                );
            this.toolTipInfo.SetToolTip(this.checkBoxRecursive, 
                "If you are dragging folders to import, with this option ticked, it will search all subfolders inside that folder for pkg files"
                );
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            SaveSettings();
            if(!server.Equals(default(Server))){
                server.StopServer();
            }
        }

        private void PopulateValidIPs() {
            foreach(var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList) {
                if(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                    comboBoxServerIP.Items.Add(ip.ToString());
                }
            }

            if(comboBoxServerIP.Items.Count > 0) {
                comboBoxServerIP.SelectedIndex = 0;
            }
        }

      
        private void buttonProcessQueue_Click(object sender, EventArgs e) {
            if (processing) {
                buttonProcessQueue.Text = "Process Queue";
                processing = false;
                queueBackgroundWorker.CancelAsync();
            } else {
                if (queueBackgroundWorker.IsBusy) {
                    MessageBox.Show("The background worker is still busy.\nPlease wait some seconds before trying again.", "Error");
                    return;
                }
                if(ps4PkgList.Count == 0) {
                    MessageBox.Show("The queue is empty. Please add some items first", "Error");
                    return;
                }
                if (!connected) {
                    MessageBox.Show("Please check your PS4's IP or ensure your firewall is not blocking the connection.","Could Not Establish Connection");
                    return;
                }
                processing = true;
                buttonProcessQueue.Text = "Stop Queue";
                queueBackgroundWorker.RunWorkerAsync(GetServerDetails());
            }
        }

        private bool IsValidIP() {
            return textBoxPS4IP.Text.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(textBoxPS4IP.Text, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");
        }

        private void ToggleConnected(bool connected) {
            this.connected = connected;
            labelConnectionDisplay.ForeColor = connected ? Color.Green : Color.Red;
            labelConnectionDisplay.Text  = connected ? "Connected" : "Not Connected";
        }

        private void timer1_Tick(object sender, EventArgs e) {
            if (IsValidIP() && !connected) {
                using (var pingSender = new Ping()) {
                    var pingOptions = new PingOptions() {
                        DontFragment = true,
                        Ttl = 5
                    };

                    var pingData = Encoding.ASCII.GetBytes("hello world");
                    const int timeout = 60;
                    try {
                        var reply = pingSender.Send(textBoxPS4IP.Text, timeout, pingData, pingOptions);
                        if (reply.Status == IPStatus.Success) {
                            ToggleConnected(true);
                            Logger.WriteLine("Successfully Pinged Device",Logger.Type.DebugOutput);
                        } else {
                            ToggleConnected(false);
                        }
                    } catch {

                    }
                }
            }
        }

        private void textBoxPS4IP_TextChanged(object sender, EventArgs e) {
            ToggleConnected(false);
        }

        #region ListView Stuff

        private void ColumnWidth(ColumnHeader header, int width) {
            listViewItemsQueue.InvokeIfRequired(() => header.Width = width);
        }

        ScrollBars GetControlVisibleScrollbars(Control ctl) {
            int wndStyle = Win32Api.GetWindowLong(listViewHwnd, Win32Api.GWL_STYLE);
            bool hsVisible = (wndStyle & Win32Api.WS_HSCROLL) != 0;
            bool vsVisible = (wndStyle & Win32Api.WS_VSCROLL) != 0;

            if (hsVisible)
                return vsVisible ? ScrollBars.Both : ScrollBars.Horizontal;
            else
                return vsVisible ? ScrollBars.Vertical : ScrollBars.None;
        }

        public ScrollBars GetVisibleScrollbars() {
            return GetControlVisibleScrollbars(listViewItemsQueue);
        }

        public void ResizeListViewColumns(ListView listView, IntPtr hwnd) {
            int i = 0;
            int TotalWidthUsed = 0;
            int Offset = 40;
            listView.InvokeIfRequired(() => listView.SuspendLayout());
            foreach (ColumnHeader item in listView.Columns) {
                int itemCount = listView.Items.Count;
                if (i == listView.Columns.Count - 1) {
                    int offsetWidth = 12;

                    if ((GetVisibleScrollbars() & ScrollBars.Vertical) == ScrollBars.Vertical) {
                        offsetWidth += 17;
                    }
                    int remainingWidth = (this.Width) - TotalWidthUsed - offsetWidth;
                    ColumnWidth(item, remainingWidth);
                } else {
                    if (itemCount > 0) {
                        int max = 0;
                        listView.InvokeIfRequired(() => {
                            int j = 0;
                            foreach (ListViewItem lvitem in listView.Items) {
                                int subItemCount = listView.Items[j].SubItems.Count - 1;
                                int index = item.Index > (subItemCount) ? subItemCount : item.Index;
                                var size = TextRenderer.MeasureText(listView.Items[j].SubItems[index].Text, listView.Font).Width;
                                if (size > max) {
                                    max = size;
                                }
                                ++j;
                            }
                        });
                        switch (item.Text) {
                            case "File Name":

                            break;

                            case "Title":
                            if (max < 220) {
                                ColumnWidth(item, max + 8 + Offset);
                            }
                            break;

                            default:
                            ColumnWidth(item, max + 8 + Offset);
                            break;
                        }
                    } else {
                        ColumnWidth(item, TextRenderer.MeasureText(item.Text, listView.Font).Width + 8 + Offset);
                    }

                    listView.InvokeIfRequired(() => TotalWidthUsed += item.Width);
                }

                listView.InvokeIfRequired(() => listView.ResumeLayout());
                ++i;
            }
            int maxTotalWidth = 797;
            listView.InvokeIfRequired(() => {
                this.listViewItemsQueue.Columns[listViewItemsQueue.Columns.Count - 1].Width = Math.Abs(maxTotalWidth - TotalWidthUsed - 3);
            });
        }

        private void listViewItemsQueue_DragEnter(object sender, DragEventArgs e) {
            if(e.Data.GetDataPresent(DataFormats.FileDrop)) {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void listViewItemsQueue_MouseDown(object sender, MouseEventArgs e) {
            if (processing) return;
            if (e.Button == MouseButtons.Right) {
                if (listViewItemsQueue.FocusedItem != null && listViewItemsQueue.FocusedItem.Bounds.Contains(e.Location)) {
                    contextMenuStripFocused.InvokeIfRequired(() => {
                        contextMenuStripFocused.Show(Cursor.Position);
                    });
                } else {
                    contextMenuStripNoFocus.InvokeIfRequired(() => {
                        contextMenuStripNoFocus.Show(Cursor.Position);
                    });
                }
            }
        }

        private void listViewItemsQueue_DragDrop(object sender, DragEventArgs e) {
            string[] filePaths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (filePaths != null && filePaths.Length > 0) {
                AddAllValidItems(filePaths);
            }
        }

        string[] GetFilesForFileExtension(string extension, string[] paths, bool searchDirectory) {
            List<string> fileList = new List<string>();
            foreach (var path in paths.Where(n => Path.GetExtension(n) == extension || Directory.Exists(n))) {
                if (Directory.Exists(path) && searchDirectory && checkBoxRecursive.Checked) {
                    fileList.AddRange(Directory.GetFiles(path, $"*{extension}", SearchOption.AllDirectories));
                } else if (File.Exists(path)) {
                    fileList.Add(path);
                }
            }

            return fileList.ToArray();
        }

     

        public void AutoUpdateColumnWidth(ListView lv) {
            for (int i = 0; i <= lv.Columns.Count - 1; i++) {
                lv.Columns[i].Width = -2;
            }
        }

        private void listViewItemsQueue_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e) {
            Color color = Color.FromArgb(30, 30, 30, 30);
            Color color1 = Color.FromArgb(30, 30, 30, 30);

            using (System.Drawing.Drawing2D.LinearGradientBrush GradientBrush = new System.Drawing.Drawing2D.LinearGradientBrush(e.Bounds, color, color1, 270)) {
                e.Graphics.FillRectangle(GradientBrush, e.Bounds);
            }

            Color linesColor = Color.Gray;
            using (var brush = new SolidBrush(linesColor)) {
                using (var pen = new Pen(brush)) {
                    var offset = -1;
                    var bounds = e.Bounds;
                    e.Graphics.DrawLine(pen, bounds.X, bounds.Y + bounds.Height + offset, bounds.X + bounds.Width, bounds.Y + bounds.Height + offset);
                    e.Graphics.DrawLine(pen, bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y);
                    e.Graphics.DrawLine(pen, bounds.X, bounds.Y, bounds.X, bounds.Y + bounds.Height);
                }
            }

            using (var brush = new SolidBrush(Color.White)) {
                var rect = new Rectangle(e.Bounds.Location, new Size(e.Bounds.Width, e.Bounds.Height));
                rect.Offset(2, 5);
                e.Graphics.DrawString(e.Header.Text, listViewItemsQueue.Font, brush, rect);
            }
        }

        private void listViewItemsQueue_DrawItem(object sender, DrawListViewItemEventArgs e) {
            e.DrawDefault = true;
        }

        private void listViewItemsQueue_DrawSubItem(object sender, DrawListViewSubItemEventArgs e) {
            e.DrawDefault = true;
        }

        private void MainForm_Resize(object sender, EventArgs e) {
            AutoUpdateColumnWidth(listViewItemsQueue);
        }

        #endregion

        #region Item Queue Stuff
        private void AddItem(PkgInfo pkgInfo) {
            //Add To ListView
            ListViewItem listViewItem = new ListViewItem(pkgInfo.Title);
            listViewItem.Tag = pkgInfo.FilePath;
            listViewItem.SubItems.Add(Path.GetFileName(pkgInfo.FilePath));
            listViewItem.SubItems.Add(pkgInfo.Type.ToString());
            listViewItem.SubItems.Add(Enums.TaskType.Queued.ToString());
            this.listViewItemsQueue.InvokeIfRequired(() => listViewItemsQueue.Items.Add(listViewItem));
            QueueItem queueItem = new QueueItem(listViewItem, pkgInfo);
            ps4PkgList.Add(queueItem);
        }
        
        private void AddAllValidItems(string[] paths) {
            var pkgFilePaths = GetFilesForFileExtension(".pkg", paths, true);
            List<PkgInfo> pkgList = new List<PkgInfo>();
            List<PkgInfo> patchesList = new List<PkgInfo>();
            foreach (var filePath in pkgFilePaths) {
                var pkgFilePath = filePath;
                var fileName = Path.GetFileName(pkgFilePath);
                if(fileName.Contains(" ")) {
                    fileName = fileName.Replace(" ", ".");
                    pkgFilePath = pkgFilePath.Substring(0,pkgFilePath.Length-fileName.Length) + fileName;
                    File.Move(filePath, pkgFilePath);
                }

                try {
                    var pkg = PS4_Tools.PKG.SceneRelated.Read_PKG(pkgFilePath);
                    PkgInfo pkgInfo = new PkgInfo();
                    pkgInfo.FilePath = pkgFilePath;
                    pkgInfo.TitleID = pkg.Param.TitleID;
                    pkgInfo.Title = pkg.Param.Title;
                    pkgInfo.Version = pkg.Param.APP_VER;
                    pkgInfo.Type = Enums.Parser.Parse(pkg.PKG_Type);
                    pkgInfo.ContentID = pkg.Content_ID;
                    pkgInfo.PkgFiles = new string[] { Path.GetFileName(pkgFilePath) };

                    if (pkgInfo.Type == Enums.PkgType.Patch) {
                        patchesList.Add(pkgInfo);
                    } else {
                        AddItem(pkgInfo);
                    }
                } catch { }
            }


            //Handles multiple patches edge case
            var patchDict = new Dictionary<string, List<string>>();
            foreach (var patch in patchesList) {
                if (!patchDict.ContainsKey(patch.TitleID)) {
                    patchDict.Add(patch.TitleID, new List<string>());
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(patch.FilePath, @"([A-Za-z0-9-_]+_\d.pkg)+")) {
                    patchDict[patch.TitleID].Add(Path.GetFileName(patch.FilePath));
                }
            }

            for (int i = 0; i < patchesList.Count; ++i) {
                var filePaths = patchDict[patchesList[i].TitleID];
                if (filePaths.Count == 0) {
                    filePaths.Add(Path.GetFileName(patchesList[i].FilePath));
                }
                patchesList[i].PkgFiles = filePaths.ToArray();
                AddItem(patchesList[i]);
            }

            //Order by games -> patch -> DLC -> themes so nothing goes wrong
            //This will not change the order in the list view
            //But it is not necessary anyway, as each object has an instance
            //To the UI
            ps4PkgList.Sort();
            ResizeListViewColumns(this.listViewItemsQueue, listViewHwnd);
        }
        

        public void queueWorker_DoWork(object sender, DoWorkEventArgs e) {
            Server server = (Server)e.Argument;
            Queue<QueueItem> ps4PkgQueue = new Queue<QueueItem>();
            ps4PkgList.Where(pkg => pkg.TaskType == Enums.TaskType.Queued).ToList().ForEach(p => ps4PkgQueue.Enqueue(p));
            int totalQueue = ps4PkgQueue.Count;
            long taskId = 0;
            bool finished = false;
            bool forceStopped = false;
            bool firstInitiate = true;
            bool skipInstallCheck = false;
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (ps4PkgQueue.Count > 0) {

                this.labelProgressNotify.InvokeIfRequired(() =>labelProgressNotify.Text = $"Items left in queue {ps4PkgQueue.Count} of {totalQueue}");
                var queueItem = ps4PkgQueue.Peek();

                if (queueBackgroundWorker.CancellationPending) {
                    queueItem.UpdateTask(Enums.TaskType.Queued, listViewItemsQueue);
                    forceStopped = true;
                    break;
                }

                if (!server.IsRunning) {
                    server.StartServer(queueItem.PkgInfo);
                    continue;
                }

                try {

                    try {


                        switch (queueItem.TaskType) {
                            case Enums.TaskType.Uninstalling:
                            if (server.Uninstall(queueItem.PkgInfo)) {
                                finished = true;
                                taskId = 0;
                            }
                            break;

                            case Enums.TaskType.Queued:
                            if (queueItem.Uninstall) {
                                queueItem.UpdateTask(Enums.TaskType.Uninstalling, listViewItemsQueue);
                            } else {
                                queueItem.UpdateTask(Enums.TaskType.Sending, listViewItemsQueue);
                            }
                            break;

                            case Enums.TaskType.Sending:
                            if (taskId == 0) {
                                try {
                                    if (server.InitiateInstall(queueItem.PkgInfo, skipInstallCheck,out taskId)) {
                                        if (taskId == 0xAFFFFFF) { //no task id provided but already installed
                                            finished = true;
                                        }
                                    }
                                } catch (RPIErrorThrownException ex) {
                                    bool doDefault = false;
                                    switch (ex.ErrorCode) {
                                        case 0x80990019: //SCE_BGFT_ERROR_TASK_NOENT

                                        //The API is so fked up that it returns random shit
                                        //This here will handle this weird use case
                                        //Most apps should install with this here.
                                        if (!skipInstallCheck) {
                                            taskId = 0;
                                            skipInstallCheck = true;
                                        } else {
                                            finished = true;
                                            taskId = -1;
                                        }
                                        break;

                                        case 0x80020016: //SCE_KERNEL_ERROR_EINVAL
                                        if (skipInstallCheck) {
                                            doDefault = true;
                                        } else {
                                            taskId = 0;
                                            skipInstallCheck = true;
                                        }
                                        break;

                                        default:
                                        doDefault = true;
                                        break;
                                    }

                                    if (doDefault) {
                                        throw new SkipItemException(Enums.TaskType.Failed, $"Could not install. Error: 0x{ex.ErrorCode.ToString("X")} ({ex.Message})");
                                    }
                                }
                          
                            }
                            break;
                        }

                        long timeLeft = (Settings.ProgressCheckDelay.ToMilliseconds() - stopwatch.ElapsedMilliseconds)/1000; 
                        this.progressBar1.InvokeIfRequired(() => progressBar1.ExtraText = $" ({timeLeft})");

                        if (taskId > 0 && taskId != 0xAFFFFFF && (stopwatch.ElapsedMilliseconds >= Settings.ProgressCheckDelay.ToMilliseconds() || firstInitiate)) {
                            var progress = server.GetInstallProgress(taskId);
                            if (progress != null) {
                                firstInitiate = false;
                                var total = progress.GetPercentageComplete();
                                queueBackgroundWorker.ReportProgress(total, progress);
                                if (total >= 100 || progress.ExceedsTotalLength()) {
                                    finished = true;
                                }
                                stopwatch.Restart();
                            }
                        }
                    } catch (WebException ex) { //Best not to ask why I did it like this
                        if (ex.Status == WebExceptionStatus.ProtocolError) {
                            var resp = ((HttpWebResponse)ex.Response);
                            if (resp.StatusCode == HttpStatusCode.RequestTimeout) {
                                throw new SkipItemException(Enums.TaskType.Timed_Out, "Operation timed out");
                            } else if (resp.StatusCode == HttpStatusCode.InternalServerError) {
                                Logger.WriteLine("::status error - " + ex.Message,Logger.Type.StandardOutput);
                                throw new SkipItemException(Enums.TaskType.Failed, "Something is wrong with the server. It returned HTTP 500");
                            }
                        } else if(ex.Status == WebExceptionStatus.ConnectionClosed) {
                            throw new SkipItemException(Enums.TaskType.Failed, "Remote PKG Installer has crashed. Please restart it on your PS4");
                        } else if (ex.Status == WebExceptionStatus.ConnectFailure) {
                            throw new SkipItemException(Enums.TaskType.Failed, "Couldn't Connect to RPI. Check your firewall settings or ensure that the application is running.");
                        }
                    }

                    if (finished) {
                        taskId = 0;
                        progressBar1.InvokeIfRequired(() => progressBar1.ResetProgressBar());
                        queueItem.UpdateTask(Enums.TaskType.Finished, queueItem.Uninstall ? "Uninstalled" : "Installed", listViewItemsQueue);
                        server.StopServer();
                        ps4PkgQueue.Dequeue();
                        finished = false;
                        skipInstallCheck = false;
                        firstInitiate = true;
                        System.Threading.Thread.Sleep(1000); //Sleep some seconds so we don't piss the server off
                    }

                }  catch (SkipItemException ex) { 
                    //Skip Item
                    queueItem.UpdateTask(ex.TaskType, ex.Message, listViewItemsQueue);
                    ps4PkgQueue.Dequeue();
                    server.StopServer();
                    taskId = 0;
                    skipInstallCheck = false;
                }
                System.Threading.Thread.Sleep(500);
            }
            if (!forceStopped) {
                this.labelProgressNotify.InvokeIfRequired(() => labelProgressNotify.Text = $"All Done!");
                this.InvokeIfRequired(() => buttonProcessQueue_Click(null, null));
            } else {
                this.labelProgressNotify.InvokeIfRequired(() => labelProgressNotify.Text = $"Items left in queue {ps4PkgQueue.Count} of {totalQueue}");
            }
            server.StopServer();
            connected = false;
        }

        public void queueWorker_ProgressChanged(object sender, ProgressChangedEventArgs e) {
            this.progressBar1.InvokeIfRequired((()=> {
                //When the JB fails to correctly initialize, the pkg sender breaks and sends some absurd values
                //Or it could be the fault of mira, who knows.

                //This prevents it from overflowing the progress bar
                if(e.ProgressPercentage < 0 || e.ProgressPercentage >=  int.MaxValue) {
                    return;
                }
                progressBar1.Value = e.ProgressPercentage;
                var progress = e.UserState as DataTrasmittedProgress;
                if(progress != null) {
                    progressBar1.SecondsRemaining = progress.SecondsLeft;
                }
            }));
        }

        #endregion

        private void clearAllToolStripMenuItem_Click(object sender, EventArgs e) {
            ps4PkgList.Clear();
            this.listViewItemsQueue.Items.Clear();
        }

        private void ChangeItemStatus(Action<QueueItem> action) {
            foreach (ListViewItem item in this.listViewItemsQueue.SelectedItems) {
                var q = ps4PkgList
                    .Where(queueItem => queueItem.PkgInfo.FilePath == (string)item.Tag)
                    .FirstOrDefault();
                if (q != null) {
                    action?.Invoke(q);
                }
            }
        }
        private void markForUninstallToolStripMenuItem_Click(object sender, EventArgs e) {
            ChangeItemStatus(q => {
                q.UpdateTask(Enums.TaskType.Queued, "Marked for uninstall", this.listViewItemsQueue);
                q.Uninstall = true;
            });
        }

        private void requeueItemToolStripMenuItem_Click(object sender, EventArgs e) {
            ChangeItemStatus(q => {
                q.UpdateTask(Enums.TaskType.Queued, this.listViewItemsQueue);
                q.Uninstall = false; 
            });
        }

        private void toolStripMenuItemMarkAsTheme_Click(object sender, EventArgs e) {
            ChangeItemStatus(q => {
                q.UpdateType(Enums.PkgType.Addon_Theme, this.listViewItemsQueue);
            });
            ps4PkgList.Sort(); //Re-sort so theme's get pushed to the bottom
        }

        private void label3_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            Process.Start("https://github.com/frostyso");
        }

        private void toolStripMenuItem4_Click(object sender, EventArgs e) {
            using (OpenFileDialog openFileDialog = new OpenFileDialog()) {
                openFileDialog.Multiselect = true;
                openFileDialog.Filter = "Pkg files (*.pkg)|*.pkg";
                if(openFileDialog.ShowDialog() == DialogResult.OK) {
                    AddAllValidItems(openFileDialog.FileNames);
                }
            }
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e) {
            listViewItemsQueue.BeginUpdate();
            //Delete from the reverse
            for (int i = listViewItemsQueue.Items.Count - 1; i > 0; --i) {
                if(listViewItemsQueue.Items[i].Selected)
                    listViewItemsQueue.Items.RemoveAt(i);
            }
            listViewItemsQueue.EndUpdate();
        }

        private void listViewItemsQueue_ColumnClick(object sender, ColumnClickEventArgs e) {
            ListView myListView = (ListView)sender;

            // Determine if clicked column is already the column that is being sorted.
            if (e.Column == listViewColumnSorter.SortColumn) {
                // Reverse the current sort direction for this column.
                if (listViewColumnSorter.Order == SortOrder.Ascending) {
                    listViewColumnSorter.Order = SortOrder.Descending;
                } else {
                    listViewColumnSorter.Order = SortOrder.Ascending;
                }
            } else {
                // Set the column number that is to be sorted; default to ascending.
                listViewColumnSorter.SortColumn = e.Column;
                listViewColumnSorter.Order = SortOrder.Ascending;
            }
            // Perform the sort with these new sort options.
            myListView.Sort();
        }

    
    }
}
