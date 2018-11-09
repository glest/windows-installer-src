using IWshRuntimeLibrary;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using File = System.IO.File;

namespace ZetaGlestInstaller {
	/// <summary>
	/// The installer window
	/// </summary>
	public class Installer : Form {
		private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
		private static int bitness = Marshal.SizeOf(typeof(IntPtr)) * 8;
		private RichTextBox licenseTextBox;
		private PictureBox pictureBox1;
		private Button installButton;
		private TextBox pathTextBox;
		private Label label2;
		private CheckBox desktopShortcutCheckBox;
		private CheckBox startMenuShortcutCheckBox;
		private CheckBox networkFixCheckBox;
		private ProgressBar progressBar;
		private Button pathButton;
		private int start, target; //progress is from 0 to 1000
		private static bool closing, busy;
		private static Process sevenZip;
		private static StringBuilder sevenZipOutput;
		private static ConfigParams Config;
		private static WebClient binariesClient, dataClient;
		private static ManualResetEventSlim dataResetEvent = new ManualResetEventSlim(false);
		private static int lineNumber;

		/// <summary>
		/// Constructs the installer form
		/// </summary>
		public Installer() {
			CheckForIllegalCrossThreadCalls = false;
			InitializeComponent();
			ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, policyErrors) => true;
			ServicePointManager.Expect100Continue = true;
			ServicePointManager.CheckCertificateRevocationList = false;
			ServicePointManager.SecurityProtocol = (SecurityProtocolType) 3072;
		}

		/// <summary>
		/// The main entry point for the application
		/// </summary>
		[STAThread]
		public static void Main() {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new Installer());
		}

		/// <summary>
		/// Called when the form is loaded
		/// </summary>
		protected override void OnLoad(EventArgs e) {
			base.OnLoad(e);
			licenseTextBox.SelectAll();
			licenseTextBox.SelectionAlignment = HorizontalAlignment.Center;
			licenseTextBox.DeselectAll();
			UIScaler.AddToScaler(this);
			UIScaler.ExcludeFontScaling(licenseTextBox);
			pathTextBox.Text = GetDefaultInstallPath();
		}

		private static string GetDefaultInstallPath() {
			return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar + "ZetaGlest";
		}

		/// <summary>
		/// Called when the form is shown
		/// </summary>
		protected override void OnShown(EventArgs e) {
			base.OnShown(e);
			try {
				InitConfig();
			} catch (Exception ex) {
				MessageBox.Show("Could not load configuration: " + ExceptionToString(ex), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				Close();
				return;
			}
			string version;
			string path = CheckIsInstalled(out version);
			if (path != null) {
				try {
					pathTextBox.Text = path;
					int update = new Version(Config.Version).CompareTo(new Version(version));
					if (update > 0)
						installButton.Text = "Agree && Update";
					else if (update < 0)
						installButton.Text = "Agree && Downgrade";
					else if (update == 0) {
						installButton.Text = "Agree && Reinstall";
						if (MessageBox.Show("The same version of ZetaGlest seems to already be installed. Do you want to uninstall?", "Alert", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
							Thread thread = new Thread(() => StartUninstall(false));
							thread.Name = "UninstallThread";
							thread.IsBackground = true;
							thread.Start();
						}
					}
				} catch {
				}
			}
		}

		/// <summary>
		/// Initializes the installation configuration
		/// </summary>
		private static void InitConfig() {
			if (Config == null) {
				KeyValueConfigurationCollection collection = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings;
				Config = new ConfigParams(collection["version"].Value.Trim(),
					new Uri(collection["binaries"].Value.Trim()),
					collection["binaries-md5"].Value.Trim().Replace("-", string.Empty).ToLower(),
					collection["binaries-dir"].Value.Trim(),
					new Uri(collection["data"].Value.Trim()),
					collection["data-md5"].Value.Trim().Replace("-", string.Empty).ToLower(),
					collection["data-dir"].Value.Trim(),
					int.Parse(collection["data-7zlinecount"].Value.Trim()));
			}
		}

		/// <summary>
		/// Called when the path selection button is clicked
		/// </summary>
		private void pathButton_Click(object sender, EventArgs e) {
			using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
				dialog.SelectedPath = pathTextBox.Text;
				dialog.ShowNewFolderButton = true;
				if (dialog.ShowDialog() == DialogResult.OK)
					pathTextBox.Text = dialog.SelectedPath + Path.DirectorySeparatorChar + "ZetaGlest";
			}
		}

		/// <summary>
		/// Called when the "Install" button is clicked
		/// </summary>
		private void installButton_Click(object sender, EventArgs e) {
			Thread thread = new Thread(StartInstall);
			thread.Name = "InstallThread";
			thread.IsBackground = true;
			thread.Start();
		}

		/// <summary>
		/// Sets the progress bar value
		/// </summary>
		/// <param name="value">The progress value where 0 is no progress and 1000 is finished progress</param>
		private void SetProgress(int value) {
			try {
				//Invoke(new Action(() => {
				ThreadPool.QueueUserWorkItem(val => progressBar.Value = (int) val, value);
				//}));
			} catch {
			}
		}

		/// <summary>
		/// Sets the status button text
		/// </summary>
		/// <param name="status">The text to show</param>
		private void SetButtonText(string status) {
			try {
				//Invoke(new Action(() => {
					installButton.Text = status;
				//}));
			} catch {
			}
		}

		private static string ExceptionToString(Exception ex) {
			if (ex == null)
				return string.Empty;
			try {
				using (StreamWriter log = File.CreateText("error.log"))
					log.WriteLine(ex.StackTrace);
			} catch {
			}
			StringBuilder builder = new StringBuilder();
			while (ex != null) {
				builder.Append(ex.Message + " ");
				ex = ex.InnerException;
			}
			return builder.ToString();
		}

		/// <summary>
		/// Shows a message box on the form thread
		/// </summary>
		private static DialogResult ShowMessageBox(string message, string title, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Information) {
			try {
				return /*(DialogResult) Invoke(new Func<object>(() => */MessageBox.Show(message, title, buttons, icon)/*))*/;
			} catch {
				Environment.Exit(1);
				return DialogResult.None;
			}
		}

		/// <summary>
		/// Presents a dialog that shows that the installation or uninstallation was a success
		/// </summary>
		/// <param name="uninstall">Whether what was successful was an uninstallation</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		private void ShowSuccess(bool uninstall, List<string> warnings) {
			installButton.Text = "Success!";
			StringBuilder message = new StringBuilder();
			MessageBoxIcon icon;
			if (warnings.Count == 0) {
				message.Append("ZetaGlest" + (uninstall ? " un" : " ") + "installed successfully!");
				icon = MessageBoxIcon.Information;
			} else {
				message.Append("ZetaGlest is now" + (uninstall ? " un" : " ") + "installed, although ");
				if (warnings.Count == 1)
					message.Append("some errors were");
				else
					message.Append("an error was");
				message.Append(" encountered during" + (uninstall ? " un" : " ") + "installation:\n");
				foreach (string error in warnings) {
					message.Append(" - ");
					message.AppendLine(error);
				}
				icon = MessageBoxIcon.Warning;
			}
			ShowMessageBox(message.ToString(), "Installation Status", MessageBoxButtons.OK, icon);
		}

		/// <summary>
		/// Configures the binaries to their respective path
		/// </summary>
		/// <param name="path">The path that contains the extracted binary archive</param>
		public static void ConfigureBinaries(string path) {
			string target;
			foreach (string file in Directory.GetFiles(path + Path.DirectorySeparatorChar + Config.BinariesDir + Path.DirectorySeparatorChar + "vs2017", "*", SearchOption.AllDirectories)) {
				target = path + Path.DirectorySeparatorChar + Path.GetFileName(file);
				DeleteFileIfExists(target);
				File.Move(file, target);
			}
			string location = Assembly.GetEntryAssembly().Location;
			target = path + Path.DirectorySeparatorChar + nameof(ZetaGlestInstaller) + ".exe";
			File.Copy(location, target, true);
			File.Copy(Path.ChangeExtension(location, ".exe.config"), target + ".config", true);
		}

		/// <summary>
		/// Configures the data to its respective path
		/// </summary>
		/// <param name="path">The path that contains the extracted data archive</param>
		public static void ConfigureData(string path) {
			path += Path.DirectorySeparatorChar;
			if (!string.Equals(Config.DataDir, "data", StringComparison.InvariantCultureIgnoreCase)) {
				string target = path + "data";
				DeleteFolderIfExists(target);
				Directory.Move(path + Config.DataDir, target);
			}
		}

		/// <summary>
		/// Returns the MD5 checksum of the specified file
		/// </summary>
		/// <param name="file">The file whose checksum to calculate</param>
		public static string CalculateMD5(string file) {
			using (BufferedStream stream = new BufferedStream(File.OpenRead(file), 1200000)) {
				using (MD5Cng md5 = new MD5Cng())
					return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty).ToLower();
			}
		}

		/// <summary>
		/// Downloads the archives and extracts and configures the files
		/// </summary>
		/// <param name="path">The path to install ZetaGlest into</param>
		public void DownloadAndExtract(string path) {
			string binariesPath = path + Path.DirectorySeparatorChar + "binaries.zip";
			string dataPath = path + Path.DirectorySeparatorChar + "data.zip";
			dataClient = new WebClient();
			try {
				dataClient.Headers.Add("user-agent", "ZetaGlest");
				//dataClient.Proxy = GlobalProxySelection.GetEmptyWebProxy();
				dataClient.Proxy = null;
				target = 900;
				Exception downloadError = null;
				if (File.Exists(dataPath) && CalculateMD5(dataPath) == Config.DataMD5) {
					dataResetEvent.Set();
					SetProgress(start = target = 100);
				} else {
					dataResetEvent.Reset();
					DownloadProgressChangedEventHandler callback = null;
					callback = (sender, e) => {
						if (e == null || e.TotalBytesToReceive <= 0L) {
							try {
								progressBar.Style = ProgressBarStyle.Marquee;
							} catch {
							}
						} else {
							int progress = start + (int) ((target - start) * e.BytesReceived / (double) e.TotalBytesToReceive);
							if (progress < start)
								progress = start;
							else if (progress > target)
								progress = target;
							SetProgress(progress);
						}
						Application.DoEvents();
					};
					dataClient.DownloadProgressChanged += callback;
					dataClient.DownloadFileCompleted += (sender, e) => {
						dataClient.DownloadProgressChanged -= callback;
						if (e == null || e.Error == null)
							SetButtonText("Extracting data...\n(takes some time)");
						else
							downloadError = e.Error;
						dataResetEvent.Set();
					};
					dataClient.DownloadFileAsync(Config.DataUrl, dataPath);
				}
				if (File.Exists(binariesPath) && CalculateMD5(binariesPath) == Config.BinariesMD5)
					SetButtonText("Downloading data...\n(takes some time)");
				else {
					binariesClient = new WebClient();
					try {
						binariesClient.Headers.Add("user-agent", "ZetaGlest");
						//binaryClient.Proxy = GlobalProxySelection.GetEmptyWebProxy();
						binariesClient.Proxy = null;
						SetButtonText("Downloading binaries...\n(takes some time)");
						binariesClient.DownloadFile(Config.BinariesUrl, binariesPath);
					} finally {
						if (binariesClient != null) {
							binariesClient.Dispose();
							binariesClient = null;
						}
					}
				}
				SetButtonText("Downloading data...\n(takes some time)");
				if (closing)
					Thread.Sleep(-1); //pause
				else if (downloadError != null)
					throw downloadError;
				else if (CalculateMD5(binariesPath) != Config.BinariesMD5)
					throw new WarningException("MD5 hash of binaries.zip does not match the one specified in config file");
				Extract(binariesPath, path, false);
				ConfigureBinaries(path);
				dataResetEvent.Wait();
				if (downloadError != null)
					throw downloadError;
				else if (closing)
					return;
				SetButtonText("Extracting...");
				start = target;
				target = 980;
				try {
					//Invoke(new Action(() => {
						progressBar.Style = ProgressBarStyle.Continuous;
					//}));
				} catch {
					if (closing)
						Thread.Sleep(-1); //pause
				}
				if (CalculateMD5(dataPath) != Config.DataMD5)
					throw new WarningException("MD5 hash of data.zip does not match the one specified in config file");
				Extract(dataPath, path, true);
				ConfigureData(path);
				target = 1000;
				SetProgress(target);
			} finally {
				if (dataClient != null) {
					dataClient.Dispose();
					dataClient = null;
				}
			}
		}

		/// <summary>
		/// Extracts the specified archive into the specified directory
		/// </summary>
		/// <param name="path">The path to the archive to extract</param>
		/// <param name="dir">The directory to extract to</param>
		/// <param name="monitor">Whether to monitor the extraction progress</param>
		public void Extract(string path, string dir, bool monitor) {
			lineNumber = 0;
			sevenZipOutput = new StringBuilder();
			sevenZip = new Process();
			try {
				sevenZip.StartInfo.FileName = "7z.exe";
				sevenZip.StartInfo.Arguments = "x -y -o\"" + dir + "\" \"" + path + "\"";
				sevenZip.StartInfo.UseShellExecute = false;
				sevenZip.StartInfo.CreateNoWindow = true;
				sevenZip.StartInfo.RedirectStandardOutput = true;
				sevenZip.OutputDataReceived += (sender, e) => {
					sevenZipOutput.AppendLine(e.Data);
					if (monitor) {
						lineNumber++;
						int progress = start + (int) ((target - start) * lineNumber / (double) Config.Data7zLineCount);
						if (progress < start)
							progress = start;
						else if (progress > target)
							progress = target;
						SetProgress(progress);
						Application.DoEvents();
					}
				};
				if (!sevenZip.Start())
					throw new FileLoadException("7z could not start");
				sevenZip.BeginOutputReadLine();
				do {
					Application.DoEvents();
				} while (!sevenZip.WaitForExit(400));
			} finally {
				if (sevenZip != null) {
					sevenZip.Dispose();
					sevenZip = null;
				}
			}
			string output = sevenZipOutput.ToString();
			int index = output.IndexOf("Error:");
			if (index != -1)
				throw new ArgumentException(output.Substring(index + 6).Trim().Replace("\n", "").Replace("\r", ""));
		}

		/// <summary>
		/// Recursively calculates the directory size
		/// </summary>
		/// <param name="dir">The directory whose size to calculate.</param>
		[CLSCompliant(false)]
		public static ulong CalcutateDirectorySize(DirectoryInfo dir) {
			ulong size = 0ul;
			foreach (FileInfo file in dir.GetFiles()) //add file sizes
				size += (ulong) file.Length;
			foreach (DirectoryInfo d in dir.GetDirectories()) //add subdirectory sizes
				size += CalcutateDirectorySize(d);
			return size;
		}

		/// <summary>
		/// Registers the application and configures registry
		/// </summary>
		/// <param name="path">The path to the installed files</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public void Register(string path, List<string> warnings) {
			try {
				using (RegistryKey parent = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, true)) {
					if (parent == null)
						throw new Exception("Uninstall registry key not found.");
					RegistryKey key = null;
					try {
						string guidString = GetGuidString();
						key = parent.OpenSubKey(guidString, true) ??
							 parent.CreateSubKey(guidString);
						if (key == null)
							throw new KeyNotFoundException(string.Format("Unable to create uninstaller '{0}\\{1}'", UninstallKeyPath, guidString));
						string uninstallerPath = "\"" + path + "\\" + nameof(ZetaGlestInstaller) + ".exe\"";
						key.SetValue("DisplayName", "ZetaGlest");
						key.SetValue("ApplicationVersion", Config.Version);
						key.SetValue("Publisher", "ZetaGlest Team");
						key.SetValue("DisplayIcon", uninstallerPath);
						key.SetValue("DisplayVersion", Config.Version);
						key.SetValue("URLInfoAbout", "https://zetaglest.github.io/");
						key.SetValue("Contact", "zetaglest@gmail.com");
						key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
						key.SetValue("UninstallString", uninstallerPath);
						key.SetValue("EstimatedSize", (int) (CalcutateDirectorySize(new DirectoryInfo(path)) / 1024ul), RegistryValueKind.DWord);
					} finally {
						if (key != null)
							key.Close();
					}
				}
				if (networkFixCheckBox.Checked) {
					using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true)) {
						if (key == null)
							throw new Exception("Network throttling key not found.");
						try {
							key.SetValue("NetworkThrottlingIndex", -1, RegistryValueKind.DWord);
						} finally {
							if (key != null)
								key.Close();
						}
					}
				}
			} catch (Exception e) {
				warnings.Add("Could not write to registry: " + ExceptionToString(e));
			}
		}

		/// <summary>
		/// Creates the selected shortcuts
		/// </summary>
		/// <param name="path">The path to the installed files</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public void CreateShortcuts(string path, List<string> warnings) {
			string exePath = path + Path.DirectorySeparatorChar + "zetaglest-" + bitness + ".exe";
			if (startMenuShortcutCheckBox.Checked) {
				try {
					string shortcutDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "ZetaGlest");
					Directory.CreateDirectory(shortcutDirectory);
					WshShell shell = new WshShell();
					IWshShortcut shortcut = (IWshShortcut) shell.CreateShortcut(Path.Combine(shortcutDirectory, "ZetaGlest.lnk"));
					shortcut.Description = "ZetaGlest 3D RTS Game";
					shortcut.WorkingDirectory = path;
					shortcut.TargetPath = exePath;
					shortcut.Save();

					shortcut = (IWshShortcut) shell.CreateShortcut(Path.Combine(shortcutDirectory, "Map Editor.lnk"));
					shortcut.Description = "Map Editor for ZetaGlest 3D RTS Game";
					shortcut.WorkingDirectory = path;
					shortcut.TargetPath = path + Path.DirectorySeparatorChar + "map_editor-" + bitness + ".exe";
					shortcut.Save();

					shortcut = (IWshShortcut) shell.CreateShortcut(Path.Combine(shortcutDirectory, "G3D Viewer.lnk"));
					shortcut.Description = "Model Viewer for ZetaGlest 3D RTS Game";
					shortcut.WorkingDirectory = path;
					shortcut.TargetPath = path + Path.DirectorySeparatorChar + "g3d_viewer-" + bitness + ".exe";
					shortcut.Save();

					shortcut = (IWshShortcut) shell.CreateShortcut(Path.Combine(shortcutDirectory, "Uninstall ZetaGlest.lnk"));
					shortcut.Description = "Uninstalls ZetaGlest 3D RTS Game";
					shortcut.WorkingDirectory = path;
					shortcut.TargetPath = path + Path.DirectorySeparatorChar + nameof(ZetaGlestInstaller) + ".exe";
					shortcut.Save();
				} catch (Exception e) {
					warnings.Add("Could not add start menu shortcut: " + ExceptionToString(e));
				}
			}
			if (desktopShortcutCheckBox.Checked) {
				try {
					IWshShortcut shortcut = (IWshShortcut) new WshShell().CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + Path.DirectorySeparatorChar + "ZetaGlest.lnk");
					shortcut.Description = "ZetaGlest 3D RTS Game";
					shortcut.WorkingDirectory = path;
					shortcut.TargetPath = exePath;
					shortcut.Save();
				} catch (Exception e) {
					warnings.Add("Could not add desktop shortcut: " + ExceptionToString(e));
				}
			}
		}

		/// <summary>
		/// Installs ZetaGlest to the specified path
		/// </summary>
		/// <param name="path">The path to install ZetaGlest in</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public void Install(string path, List<string> warnings) {
			Directory.CreateDirectory(path);
			DownloadAndExtract(path);
			DeleteTemp(path, warnings);
			Register(path, warnings);
			CreateShortcuts(path, warnings);
		}

		/// <summary>
		/// Deletes the specified file if it exists
		/// </summary>
		/// <param name="path">The path to the file to delete</param>
		/// <param name="timeout">The approximate timeout in milliseconds</param>
		public static void DeleteFileIfExists(string path, int timeout = int.MaxValue) {
			if (File.Exists(path)) {
				File.Delete(path);
				int counter = 0;
				while (File.Exists(path)) {
					Thread.Sleep(50);
					counter += 50;
					if (counter >= timeout)
						break;
				}
			}
		}

		private static void DeleteFolderInner(string path) {
			foreach (string directory in Directory.GetDirectories(path))
				DeleteFolderInner(directory);
			try {
				Directory.Delete(path, true);
			} catch (DirectoryNotFoundException) {
			} catch (IOException) { //quantum weirdness
				Thread.Sleep(50);
				Directory.Delete(path, true);
				Thread.Sleep(50);
			} catch (UnauthorizedAccessException) { //quantum weirdness
				Thread.Sleep(50);
				Directory.Delete(path, true);
				Thread.Sleep(50);
			} 
		}

		/// <summary>
		/// Deletes the specified folder if it exists
		/// </summary>
		/// <param name="path">The path to the folder to delete</param>
		/// <param name="timeout">The approximate timeout in milliseconds</param>
		public static void DeleteFolderIfExists(string path, int timeout = int.MaxValue) {
			if (Directory.Exists(path)) {
				DeleteFolderInner(path);
				int counter = 0;
				while (Directory.Exists(path)) {
					Thread.Sleep(50);
					counter += 50;
					if (counter >= timeout)
						break;
				}
			}
		}

		/// <summary>
		/// Cleans up all temporary files and directories
		/// </summary>
		/// <param name="path">The path where the installation is located</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public static void DeleteTemp(string path, List<string> warnings) {
			try {
				DeleteFileIfExists(path + Path.DirectorySeparatorChar + "binaries.zip");
			} catch (Exception e) {
				warnings.Add("Could not delete binaries.zip: " + ExceptionToString(e));
			}
			try {
				DeleteFileIfExists(path + Path.DirectorySeparatorChar + "data.zip");
			} catch (Exception e) {
				warnings.Add("Could not delete data.zip: " + ExceptionToString(e));
			}
			try {
				DeleteFolderIfExists(path + Path.DirectorySeparatorChar + Config.BinariesDir);
			} catch (Exception e) {
				warnings.Add("Could not delete " + Config.BinariesDir + ": " + ExceptionToString(e));
			}
			try {
				DeleteFolderIfExists(path + Path.DirectorySeparatorChar + Config.DataDir);
			} catch (Exception e) {
				warnings.Add("Could not delete " + Config.DataDir + ": " + ExceptionToString(e));
			}
		}

		/// <summary>
		/// Deletes the ZetaGlest shortcuts
		/// </summary>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public static void DeleteShortcuts(List<string> warnings) {
			try {
				DeleteFolderIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "ZetaGlest"));
			} catch (Exception e) {
				warnings.Add("Could not delete start menu shortcut: " + ExceptionToString(e));
			}
			try {
				DeleteFileIfExists(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + Path.DirectorySeparatorChar + "ZetaGlest.lnk");
			} catch (Exception e) {
				warnings.Add("Could not delete desktop shortcut: " + ExceptionToString(e));
			}
		}

		private static string GetGuidString() {
			return new Guid(((GuidAttribute) typeof(Installer).Assembly.GetCustomAttributes(typeof(GuidAttribute), true)[0]).Value).ToString("B");
		}

		/// <summary>
		/// Deletes the ZetaGlest registry keys
		/// </summary>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public static void DeleteRegistryKeys(List<string> warnings) {
			try {
				using (RegistryKey parent = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, true)) {
					if (parent != null)
						parent.DeleteSubKeyTree(GetGuidString(), false);
				}
			} catch (Exception e) {
				warnings.Add("Could not delete registry key: " + ExceptionToString(e));
			}
		}

		/// <summary>
		/// Removes all installation files and traces
		/// </summary>
		/// <param name="path">The path where the installation is located</param>
		/// <param name="warnings">The warnings list to add to if an error happens</param>
		public static void Uninstall(string path, List<string> warnings) {
			DeleteFolderIfExists(path);
			DeleteShortcuts(warnings);
			DeleteRegistryKeys(warnings);
		}

		private void ResetUI() {
			try {
				if (IsHandleCreated && Visible) {
					//Invoke(new Action(() => {
						installButton.Text = "Agree && Install";
						installButton.Enabled = true;
						pathButton.Enabled = true;
						progressBar.Value = 0;
						start = 0;
					//}));
				}
			} catch {
				Environment.Exit(0);
			}
		}

		private string StartProgressUI(string message) {
			/*return (string) Invoke(new Func<object>(() => {*/
				progressBar.Value = 0;
				start = 0;
				installButton.Enabled = false;
				pathButton.Enabled = false;
				installButton.Text = message;
				return pathTextBox.Text;
			/*}));*/
		}

		/// <summary>
		/// Removes all installation files and traces
		/// </summary>
		/// <param name="silent">Whether to present any messages to the user</param>
		public void StartUninstall(bool silent) {
			lock (this) {
				busy = true;
				string path = null;
				try {
					if (silent)
						path = /*(string) Invoke(new Func<object>(() => */pathTextBox.Text/*))*/;
					else
						path = StartProgressUI("Uninstalling...");
					List <string> warnings = new List<string>();
					Uninstall(path, warnings);
					busy = false;
					if (!silent)
						ShowSuccess(true, warnings);
				} catch (Exception ex) {
					if (!silent && IsHandleCreated && Visible && ShowMessageBox("An error occurred: " + ExceptionToString(ex) + ". Try running as administrator if the issue persists. Do you want to retry?", "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
						StartUninstall(false);
				} finally {
					busy = false;
					if (!silent)
						ResetUI();
				}
			}
		}

		/// <summary>
		/// Returns the path where ZetaGlest is installed if it is found
		/// </summary>
		/// <param name="version">The currently installed version of ZetaGlest</param>
		public static string CheckIsInstalled(out string version) {
			version = null;
			try {
				using (RegistryKey parent = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, true)) {
					if (parent == null)
						throw new Exception("Uninstall registry key not found.");
					RegistryKey key = null;
					try {
						string guidString = GetGuidString();
						key = parent.OpenSubKey(guidString, true);
						if (key == null)
							return null;
						version = key.GetValue("ApplicationVersion").ToString();
						return Path.GetDirectoryName(key.GetValue("UninstallString").ToString().Trim('"'));
					} finally {
						if (key != null)
							key.Close();
					}
				}
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Starts the installation
		/// </summary>
		public void StartInstall() {
			lock (this) {
				busy = true;
				string path = null;
				try {
					path = StartProgressUI("Installing...\n(takes some time)");
					StartUninstall(true);
					busy = true;
					List<string> warnings = new List<string>();
					Install(path, warnings);
					busy = false;
					ShowSuccess(false, warnings);
					if (warnings.Count == 0) {
						//try {
						/*Invoke(new Action(Close));*/
						Close();
						//} catch {
						//}
					}
				} catch (Exception ex) {
					if (IsHandleCreated && Visible) {
						switch (ShowMessageBox("An error occurred: " + ExceptionToString(ex) + ". Try running as administrator or changing the install path if the issue persists. Do you want to retry, or clean up?", "Error", MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Error)) {
							case DialogResult.Retry:
								StartInstall();
								break;
							case DialogResult.Abort:
								StartUninstall(false);
								break;
							default:
								return;
						}
					}
				} finally {
					busy = false;
					ResetUI();
				}
			}
		}

		/// <summary>
		/// Called when the window is being closed
		/// </summary>
		/// <param name="e">Whether to cancel the window close event</param>
		protected override void OnClosing(CancelEventArgs e) {
			if (busy) {
				if (MessageBox.Show("The current process is not finished yet. Are you sure you want to exit?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
					closing = true;
					if (sevenZip != null) {
						try {
							Process sevenZ = sevenZip;
							sevenZip = null;
							if (sevenZ != null)
								sevenZ.Kill();
							sevenZ.WaitForExit(2500);
							Thread.Sleep(100);
						} catch {
						}
					}
					WebClient client = dataClient;
					if (client != null) {
						try {
							client.CancelAsync();
							dataResetEvent.Wait();
							Thread.Sleep(1000);
						} catch {
						}
					}
					client = binariesClient;
					if (client != null) {
						try {
							client.CancelAsync();
							Thread.Sleep(1000);
						} catch {
						}
					}
					try {
						Uninstall(pathTextBox.Text, new List<string>());
					} catch {
					}
				} else
					e.Cancel = true;
			}
			base.OnClosing(e);
		}

		/// <summary>
		/// Disposes of the resources used by the form
		/// </summary>
		/// <param name="disposing">Whether to dispose managed resources</param>
		protected override void Dispose(bool disposing) {
			Environment.Exit(0); //to prevent hanging bug
			//base.Dispose(disposing);
		}

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent() {
			System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Installer));
			this.licenseTextBox = new System.Windows.Forms.RichTextBox();
			this.installButton = new System.Windows.Forms.Button();
			this.pathTextBox = new System.Windows.Forms.TextBox();
			this.label2 = new System.Windows.Forms.Label();
			this.pathButton = new System.Windows.Forms.Button();
			this.pictureBox1 = new System.Windows.Forms.PictureBox();
			this.desktopShortcutCheckBox = new System.Windows.Forms.CheckBox();
			this.startMenuShortcutCheckBox = new System.Windows.Forms.CheckBox();
			this.networkFixCheckBox = new System.Windows.Forms.CheckBox();
			this.progressBar = new System.Windows.Forms.ProgressBar();
			((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
			this.SuspendLayout();
			// 
			// licenseTextBox
			// 
			this.licenseTextBox.BackColor = System.Drawing.Color.White;
			this.licenseTextBox.ForeColor = System.Drawing.Color.Black;
			this.licenseTextBox.Location = new System.Drawing.Point(12, 253);
			this.licenseTextBox.Margin = new System.Windows.Forms.Padding(8);
			this.licenseTextBox.Name = "licenseTextBox";
			this.licenseTextBox.ReadOnly = true;
			this.licenseTextBox.Size = new System.Drawing.Size(431, 200);
			this.licenseTextBox.TabIndex = 0;
			this.licenseTextBox.Text = resources.GetString("licenseTextBox.Text");
			// 
			// installButton
			// 
			this.installButton.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.installButton.ForeColor = System.Drawing.Color.Black;
			this.installButton.Location = new System.Drawing.Point(283, 12);
			this.installButton.Name = "installButton";
			this.installButton.Size = new System.Drawing.Size(160, 121);
			this.installButton.TabIndex = 3;
			this.installButton.Text = "Agree && Install";
			this.installButton.UseVisualStyleBackColor = true;
			this.installButton.Click += new System.EventHandler(this.installButton_Click);
			// 
			// pathTextBox
			// 
			this.pathTextBox.Font = new System.Drawing.Font("Calibri", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.pathTextBox.Location = new System.Drawing.Point(66, 150);
			this.pathTextBox.Multiline = true;
			this.pathTextBox.Name = "pathTextBox";
			this.pathTextBox.ReadOnly = true;
			this.pathTextBox.Size = new System.Drawing.Size(319, 23);
			this.pathTextBox.TabIndex = 4;
			// 
			// label2
			// 
			this.label2.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.label2.Location = new System.Drawing.Point(10, 150);
			this.label2.Name = "label2";
			this.label2.Size = new System.Drawing.Size(50, 23);
			this.label2.TabIndex = 5;
			this.label2.Text = "Path:";
			this.label2.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
			// 
			// pathButton
			// 
			this.pathButton.Font = new System.Drawing.Font("Calibri", 11.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.pathButton.ForeColor = System.Drawing.Color.Black;
			this.pathButton.Location = new System.Drawing.Point(396, 150);
			this.pathButton.Name = "pathButton";
			this.pathButton.Size = new System.Drawing.Size(45, 23);
			this.pathButton.TabIndex = 6;
			this.pathButton.Text = "···";
			this.pathButton.UseVisualStyleBackColor = true;
			this.pathButton.Click += new System.EventHandler(this.pathButton_Click);
			// 
			// pictureBox1
			// 
			this.pictureBox1.BackgroundImage = global::ZetaGlestInstaller.Properties.Resources.banner;
			this.pictureBox1.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom;
			this.pictureBox1.Location = new System.Drawing.Point(3, 12);
			this.pictureBox1.Name = "pictureBox1";
			this.pictureBox1.Size = new System.Drawing.Size(264, 121);
			this.pictureBox1.TabIndex = 2;
			this.pictureBox1.TabStop = false;
			// 
			// desktopShortcutCheckBox
			// 
			this.desktopShortcutCheckBox.Checked = true;
			this.desktopShortcutCheckBox.CheckState = System.Windows.Forms.CheckState.Checked;
			this.desktopShortcutCheckBox.Font = new System.Drawing.Font("Calibri", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.desktopShortcutCheckBox.Location = new System.Drawing.Point(15, 188);
			this.desktopShortcutCheckBox.Name = "desktopShortcutCheckBox";
			this.desktopShortcutCheckBox.Size = new System.Drawing.Size(128, 24);
			this.desktopShortcutCheckBox.TabIndex = 7;
			this.desktopShortcutCheckBox.Text = "Desktop Shortcut";
			this.desktopShortcutCheckBox.UseVisualStyleBackColor = true;
			// 
			// startMenuShortcutCheckBox
			// 
			this.startMenuShortcutCheckBox.Checked = true;
			this.startMenuShortcutCheckBox.CheckState = System.Windows.Forms.CheckState.Checked;
			this.startMenuShortcutCheckBox.Font = new System.Drawing.Font("Calibri", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.startMenuShortcutCheckBox.Location = new System.Drawing.Point(149, 191);
			this.startMenuShortcutCheckBox.Name = "startMenuShortcutCheckBox";
			this.startMenuShortcutCheckBox.Size = new System.Drawing.Size(139, 19);
			this.startMenuShortcutCheckBox.TabIndex = 8;
			this.startMenuShortcutCheckBox.Text = "Start Menu Shortcut";
			this.startMenuShortcutCheckBox.UseVisualStyleBackColor = true;
			// 
			// networkFixCheckBox
			// 
			this.networkFixCheckBox.Checked = true;
			this.networkFixCheckBox.CheckState = System.Windows.Forms.CheckState.Checked;
			this.networkFixCheckBox.Font = new System.Drawing.Font("Calibri", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
			this.networkFixCheckBox.Location = new System.Drawing.Point(294, 191);
			this.networkFixCheckBox.Name = "networkFixCheckBox";
			this.networkFixCheckBox.Size = new System.Drawing.Size(140, 19);
			this.networkFixCheckBox.TabIndex = 9;
			this.networkFixCheckBox.Text = "Network Throttle Fix";
			this.networkFixCheckBox.UseVisualStyleBackColor = true;
			// 
			// progressBar
			// 
			this.progressBar.Location = new System.Drawing.Point(12, 219);
			this.progressBar.Maximum = 1000;
			this.progressBar.Name = "progressBar";
			this.progressBar.Size = new System.Drawing.Size(431, 23);
			this.progressBar.Style = System.Windows.Forms.ProgressBarStyle.Continuous;
			this.progressBar.TabIndex = 10;
			// 
			// Installer
			// 
			this.AcceptButton = this.installButton;
			this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.BackColor = System.Drawing.Color.MidnightBlue;
			this.ClientSize = new System.Drawing.Size(453, 463);
			this.Controls.Add(this.progressBar);
			this.Controls.Add(this.networkFixCheckBox);
			this.Controls.Add(this.startMenuShortcutCheckBox);
			this.Controls.Add(this.desktopShortcutCheckBox);
			this.Controls.Add(this.pathButton);
			this.Controls.Add(this.label2);
			this.Controls.Add(this.pathTextBox);
			this.Controls.Add(this.installButton);
			this.Controls.Add(this.pictureBox1);
			this.Controls.Add(this.licenseTextBox);
			this.ForeColor = System.Drawing.Color.White;
			this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
			this.MaximizeBox = false;
			this.MinimumSize = new System.Drawing.Size(450, 480);
			this.Name = "Installer";
			this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
			this.Text = "ZetaGlest Installer";
			((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
			this.ResumeLayout(false);
			this.PerformLayout();

		}
	}
}