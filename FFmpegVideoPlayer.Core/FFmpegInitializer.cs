using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace FFmpegVideoPlayer.Core;

/// <summary>
/// Handles FFmpeg initialization for cross-platform video playback.
/// On macOS, can automatically install FFmpeg via Homebrew if not present.
///
/// Platform Support:
///
/// Windows (x64/x86/ARM64): Install FFmpeg via winget, chocolatey, or download binaries.
///                          winget install ffmpeg
///                          choco install ffmpeg
///
/// macOS (Intel x64/ARM64): Automatic installation via Homebrew supported!
///                          Or manually: brew install ffmpeg
///
/// Linux (x64/ARM64):       Install via package manager.
///                          sudo apt install ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev
///
/// Note: This library uses FFmpeg.AutoGen 8.x which requires FFmpeg 8.x libraries (libavcodec.62).
/// </summary>
public static class FFmpegInitializer
{
	private static bool _isInitialized;

	private static string? _ffmpegPath;

	private static string? _initializationError;

	/// <summary>
	/// Gets whether FFmpeg has been successfully initialized.
	/// </summary>
	public static bool IsInitialized => _isInitialized;

	/// <summary>
	/// Gets the path to the FFmpeg library directory being used, or null if using system default.
	/// </summary>
	public static string? FFmpegPath => _ffmpegPath;

	/// <summary>
	/// Gets any error message from initialization, or null if successful.
	/// </summary>
	public static string? InitializationError => _initializationError;

	/// <summary>
	/// Gets the detected platform and architecture (e.g., "macos-arm64", "windows-x64").
	/// </summary>
	public static string PlatformInfo => GetPlatformName() + "-" + GetArchitectureName();

	/// <summary>
	/// Determines if the current system is running on ARM architecture.
	/// </summary>
	public static bool IsArm
	{
		get
		{
			if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
			{
				return RuntimeInformation.ProcessArchitecture == Architecture.Arm;
			}
			return true;
		}
	}

	/// <summary>
	/// Determines if the current system is running on x64 architecture.
	/// </summary>
	public static bool IsX64 => RuntimeInformation.ProcessArchitecture == Architecture.X64;

	/// <summary>
	/// Determines if the current system is running on x86 architecture.
	/// </summary>
	public static bool IsX86 => RuntimeInformation.ProcessArchitecture == Architecture.X86;

	/// <summary>
	/// Determines if the current system is macOS.
	/// </summary>
	public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

	/// <summary>
	/// Determines if the current system is Windows.
	/// </summary>
	public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

	/// <summary>
	/// Determines if the current system is Linux.
	/// </summary>
	public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

	/// <summary>
	/// Event raised with status messages during initialization.
	/// </summary>
	public static event Action<string>? StatusChanged;

	private static string GetPlatformName()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return "windows";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return "macos";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return "linux";
		}
		return "unknown";
	}

	private static string GetArchitectureName()
	{
		return RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => "x64", 
			Architecture.X86 => "x86", 
			Architecture.Arm64 => "arm64", 
			Architecture.Arm => "arm", 
			_ => "unknown", 
		};
	}

	/// <summary>
	/// Initializes FFmpeg with system-installed libraries or custom binaries.
	/// On macOS, automatically installs FFmpeg via Homebrew if not found and autoInstall is true.
	/// Call this method BEFORE creating any Avalonia windows or media player instances.
	/// Typically called at the very start of Main() in Program.cs.
	/// </summary>
	/// <param name="customPath">Optional custom path to FFmpeg libraries. If provided, this path is checked FIRST before bundled binaries or system discovery. Use this to avoid conflicts with bundled binaries or to use your own FFmpeg installation.</param>
	/// <param name="autoInstall">If true, automatically install FFmpeg on macOS via Homebrew if not found. Default is true.</param>
	/// <param name="useBundledBinaries">If true, checks for bundled binaries in the NuGet package (runtimes/&lt;rid&gt;/native). If false, skips bundled binary search entirely. Default is true. Set to false to avoid conflicts with other libraries or to reduce package size.</param>
	/// <returns>True if initialization succeeded, false otherwise.</returns>
	/// <exception cref="T:FFmpegVideoPlayer.Core.FFmpegNotFoundException">Thrown when FFmpeg is not installed and cannot be auto-installed.</exception>
	public static bool Initialize(string? customPath = null, bool autoInstall = true, bool useBundledBinaries = true)
	{
		if (_isInitialized)
		{
			return true;
		}
		try
		{
			FFmpegInitializer.StatusChanged?.Invoke("Initializing FFmpeg for " + PlatformInfo + "...");
			if (!string.IsNullOrEmpty(customPath) && Directory.Exists(customPath) && FFmpegPathResolver.HasFFmpegLibrary(customPath))
			{
				FFmpegPathResolver.ConfigureNativeSearchPath(customPath);
				if (!FFmpegPathResolver.TryValidateBindings())
				{
					throw new FFmpegNotFoundException($"FFmpeg libraries at custom path '{customPath}' failed to load. The files are present but dlopen/LoadLibrary could not resolve their dependencies (common on macOS when dylibs hardcode Homebrew paths not present on this machine).\n" + GetInstallationInstructions());
				}
				_ffmpegPath = customPath;
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && useBundledBinaries)
			{
				string text = FFmpegPathResolver.TryConfigureBundledFFmpeg();
				if (!string.IsNullOrEmpty(text))
				{
					if (FFmpegPathResolver.TryValidateBindings())
					{
						_ffmpegPath = text;
					}
					else
					{
						FFmpegInitializer.StatusChanged?.Invoke("Bundled FFmpeg failed to load — searching system…");
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && string.IsNullOrEmpty(customPath))
			{
				string text2 = FindFFmpegPath(null);
				if (!string.IsNullOrEmpty(text2))
				{
					FFmpegPathResolver.ConfigureNativeSearchPath(text2);
					if (FFmpegPathResolver.TryValidateBindings())
					{
						_ffmpegPath = text2;
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && IsMacOS && autoInstall)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg not found. Installing via Homebrew (this may take a few minutes)...");
				if (TryInstallFFmpegOnMacOS())
				{
					string text3 = FindFFmpegPath(null);
					if (!string.IsNullOrEmpty(text3))
					{
						FFmpegPathResolver.ConfigureNativeSearchPath(text3);
						if (FFmpegPathResolver.TryValidateBindings())
						{
							_ffmpegPath = text3;
						}
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath) && IsLinux && autoInstall)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg not found. Attempting install via system package manager…");
				if (TryInstallFFmpegOnLinux())
				{
					string text4 = FindFFmpegPath(null);
					if (!string.IsNullOrEmpty(text4))
					{
						FFmpegPathResolver.ConfigureNativeSearchPath(text4);
						if (FFmpegPathResolver.TryValidateBindings())
						{
							_ffmpegPath = text4;
						}
					}
				}
			}
			if (string.IsNullOrEmpty(_ffmpegPath))
			{
				FFmpegPathResolver.InitializeBindings();
			}
			string text5 = "unknown";
			uint num = 0u;
			try
			{
				num = ffmpeg.avcodec_version();
				if (num != 0)
				{
					text5 = $"{num >> 16}.{(num >> 8) & 0xFF}.{num & 0xFF}";
				}
			}
			catch
			{
			}
			if (num == 0)
			{
				throw new FFmpegNotFoundException((string.IsNullOrEmpty(_ffmpegPath) ? "FFmpeg libraries could not be located or loaded." : ("FFmpeg libraries at '" + _ffmpegPath + "' loaded but no functions resolved — the native library is likely incompatible or missing transitive dependencies.")) + "\n" + GetInstallationInstructions());
			}
			_isInitialized = true;
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg initialized successfully (libavcodec: " + text5 + ")");
			return true;
		}
		catch (DllNotFoundException ex)
		{
			_initializationError = "FFmpeg libraries not found: " + ex.Message;
			FFmpegInitializer.StatusChanged?.Invoke(_initializationError);
			throw new FFmpegNotFoundException("FFmpeg libraries not found.\n" + GetInstallationInstructions(), ex);
		}
		catch (Exception ex2)
		{
			_initializationError = ex2.Message;
			FFmpegInitializer.StatusChanged?.Invoke("Failed to initialize FFmpeg: " + ex2.Message);
			throw new FFmpegNotFoundException("Failed to initialize FFmpeg: " + ex2.Message + "\n" + GetInstallationInstructions(), ex2);
		}
	}

	/// <summary>
	/// Asynchronously initializes FFmpeg with automatic installation support.
	/// On macOS, automatically installs FFmpeg via Homebrew if not found.
	/// </summary>
	/// <param name="customPath">Optional custom path to FFmpeg libraries. If provided, this path is checked FIRST before bundled binaries or system discovery.</param>
	/// <param name="autoInstall">If true, automatically install FFmpeg on macOS via Homebrew if not found.</param>
	/// <param name="useBundledBinaries">If true, checks for bundled binaries in the NuGet package. If false, skips bundled binary search entirely. Default is true.</param>
	/// <returns>True if initialization succeeded, false otherwise.</returns>
	public static async Task<bool> InitializeAsync(string? customPath = null, bool autoInstall = true, bool useBundledBinaries = true)
	{
		if (_isInitialized)
		{
			return true;
		}
		return await Task.Run(() => Initialize(customPath, autoInstall, useBundledBinaries));
	}

	/// <summary>
	/// Tries to initialize FFmpeg without throwing exceptions.
	/// </summary>
	/// <param name="customPath">Optional custom path to FFmpeg libraries. If provided, this path is checked FIRST before bundled binaries or system discovery.</param>
	/// <param name="errorMessage">Output parameter containing error message if initialization fails.</param>
	/// <param name="autoInstall">If true, automatically install FFmpeg on macOS via Homebrew if not found.</param>
	/// <param name="useBundledBinaries">If true, checks for bundled binaries in the NuGet package. If false, skips bundled binary search entirely. Default is true.</param>
	/// <returns>True if initialization succeeded, false otherwise.</returns>
	public static bool TryInitialize(string? customPath, out string? errorMessage, bool autoInstall = true, bool useBundledBinaries = true)
	{
		try
		{
			Initialize(customPath, autoInstall, useBundledBinaries);
			errorMessage = null;
			return true;
		}
		catch (FFmpegNotFoundException ex)
		{
			errorMessage = ex.Message;
			_initializationError = ex.Message;
			return false;
		}
		catch (Exception ex2)
		{
			errorMessage = ex2.Message;
			_initializationError = ex2.Message;
			return false;
		}
	}

	/// <summary>
	/// Gets the path to the Homebrew executable.
	/// </summary>
	private static string? GetHomebrewPath()
	{
		if (File.Exists("/opt/homebrew/bin/brew"))
		{
			return "/opt/homebrew/bin/brew";
		}
		if (File.Exists("/usr/local/bin/brew"))
		{
			return "/usr/local/bin/brew";
		}
		return null;
	}

	/// <summary>
	/// Attempts to install FFmpeg via Homebrew on macOS.
	/// </summary>
	/// <returns>True if installation succeeded, false otherwise.</returns>
	public static bool TryInstallFFmpegOnMacOS()
	{
		if (!IsMacOS)
		{
			return false;
		}
		string homebrewPath = GetHomebrewPath();
		if (homebrewPath == null)
		{
			FFmpegInitializer.StatusChanged?.Invoke("Installing Homebrew...");
			if (!TryInstallHomebrew())
			{
				FFmpegInitializer.StatusChanged?.Invoke("Failed to install Homebrew. Please install manually.");
				return false;
			}
			homebrewPath = GetHomebrewPath();
			if (homebrewPath == null)
			{
				return false;
			}
		}
		FFmpegInitializer.StatusChanged?.Invoke("Installing FFmpeg via Homebrew (this may take several minutes)...");
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo
			{
				FileName = homebrewPath,
				Arguments = "install ffmpeg",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			if (IsArm)
			{
				processStartInfo.Environment["PATH"] = "/opt/homebrew/bin:/opt/homebrew/sbin:" + Environment.GetEnvironmentVariable("PATH");
			}
			else
			{
				processStartInfo.Environment["PATH"] = "/usr/local/bin:/usr/local/sbin:" + Environment.GetEnvironmentVariable("PATH");
			}
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			string text = process.StandardOutput.ReadToEnd();
			string text2 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installed successfully!");
				return true;
			}
			if (text2.Contains("already installed") || text.Contains("already installed"))
			{
				return true;
			}
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installation failed: " + text2);
			return false;
		}
		catch (Exception ex)
		{
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installation error: " + ex.Message);
			return false;
		}
	}

	/// <summary>
	/// Attempts to install Homebrew on macOS.
	/// </summary>
	private static bool TryInstallHomebrew()
	{
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = "/bin/bash";
			processStartInfo.Arguments = "-c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"";
			processStartInfo.RedirectStandardOutput = true;
			processStartInfo.RedirectStandardError = true;
			processStartInfo.RedirectStandardInput = true;
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.Environment["NONINTERACTIVE"] = "1";
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			process.StandardInput.Close();
			process.StandardOutput.ReadToEnd();
			process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("Homebrew installed successfully!");
				return true;
			}
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Attempts to install FFmpeg on Linux via the system package manager (apt, dnf, or pacman).
	/// Unlike macOS/Homebrew, Linux package managers require root, so we prepend <c>sudo -n</c>
	/// (non-interactive) when not already running as root. On systems with passwordless sudo
	/// (CI, dev machines with NOPASSWD) the install succeeds silently; elsewhere it fails
	/// cleanly and the initializer surfaces an error that tells the user the exact command
	/// to run manually. We deliberately do not prompt for a password — popping up a hidden
	/// sudo prompt from inside a desktop app is surprising and hard to notice.
	/// </summary>
	/// <returns>True if installation succeeded, false otherwise.</returns>
	public static bool TryInstallFFmpegOnLinux()
	{
		if (!IsLinux)
		{
			return false;
		}
		string text = null;
		string text2;
		string text3;
		if (File.Exists("/usr/bin/apt-get"))
		{
			text = "/usr/bin/apt-get";
			text2 = "install -y ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev";
			text3 = "sudo apt install -y ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev";
		}
		else if (File.Exists("/usr/bin/dnf"))
		{
			text = "/usr/bin/dnf";
			text2 = "install -y ffmpeg ffmpeg-devel";
			text3 = "sudo dnf install -y ffmpeg ffmpeg-devel";
		}
		else
		{
			if (!File.Exists("/usr/bin/pacman"))
			{
				FFmpegInitializer.StatusChanged?.Invoke("No supported Linux package manager found. Install FFmpeg manually.");
				return false;
			}
			text = "/usr/bin/pacman";
			text2 = "-S --noconfirm ffmpeg";
			text3 = "sudo pacman -S ffmpeg";
		}
		bool flag = string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
		FFmpegInitializer.StatusChanged?.Invoke("Installing FFmpeg (this may take a few minutes)...");
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo();
			processStartInfo.FileName = (flag ? text : "sudo");
			processStartInfo.Arguments = (flag ? text2 : ("-n " + text + " " + text2));
			processStartInfo.RedirectStandardOutput = true;
			processStartInfo.RedirectStandardError = true;
			processStartInfo.UseShellExecute = false;
			processStartInfo.CreateNoWindow = true;
			processStartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
			using Process process = Process.Start(processStartInfo);
			if (process == null)
			{
				return false;
			}
			process.StandardOutput.ReadToEnd();
			string text4 = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if (process.ExitCode == 0)
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg installed successfully!");
				return true;
			}
			string text5 = text4.ToLowerInvariant();
			if (text5.Contains("password is required") || text5.Contains("a terminal is required") || text5.Contains("sudo:"))
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg auto-install needs sudo. Run manually: " + text3);
			}
			else
			{
				FFmpegInitializer.StatusChanged?.Invoke("FFmpeg install failed. Run manually: " + text3);
			}
			return false;
		}
		catch (Exception ex)
		{
			FFmpegInitializer.StatusChanged?.Invoke("FFmpeg install error: " + ex.Message + ". Run manually: " + text3);
			return false;
		}
	}

	/// <summary>
	/// Gets platform-specific FFmpeg installation instructions.
	/// </summary>
	public static string GetInstallationInstructions()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return "\r\nWINDOWS:\r\nInstall FFmpeg using one of these methods:\r\n\r\nOption 1 - WinGet (Recommended for Windows 11):\r\n    winget install ffmpeg\r\n\r\nOption 2 - Chocolatey:\r\n    choco install ffmpeg\r\n\r\nOption 3 - Manual Installation:\r\n    1. Download from https://www.gyan.dev/ffmpeg/builds/ (get the 'full' build)\r\n    2. Extract to a folder (e.g., C:\\ffmpeg)\r\n    3. Add C:\\ffmpeg\\bin to your system PATH\r\n    \r\nAfter installation, restart your terminal/IDE.";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return "\r\nmacOS (Intel x64 and Apple Silicon ARM64):\r\nInstall FFmpeg using Homebrew (supports both architectures):\r\n\r\n    brew install ffmpeg\r\n\r\nIf you don't have Homebrew installed:\r\n    /bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"\r\n    \r\nAfter installation, restart your terminal/IDE.";
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			string architectureName = GetArchitectureName();
			return "\r\nLINUX (" + architectureName + "):\r\nInstall FFmpeg using your package manager:\r\n\r\nDebian/Ubuntu:\r\n    sudo apt update\r\n    sudo apt install ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev libswresample-dev\r\n\r\nFedora:\r\n    sudo dnf install ffmpeg ffmpeg-devel\r\n\r\nArch Linux:\r\n    sudo pacman -S ffmpeg\r\n\r\nAfter installation, restart your terminal/IDE.";
		}
		return "FFmpeg libraries not found. Please install FFmpeg on your system.";
	}

	/// <summary>
	/// Checks if FFmpeg is properly installed on the system.
	/// </summary>
	public static FFmpegInstallationStatus CheckInstallation()
	{
		FFmpegInstallationStatus fFmpegInstallationStatus = new FFmpegInstallationStatus
		{
			Platform = GetPlatformName(),
			Architecture = GetArchitectureName()
		};
		try
		{
			string text = FindFFmpegPath(null);
			if (!string.IsNullOrEmpty(text))
			{
				fFmpegInstallationStatus.IsInstalled = true;
				fFmpegInstallationStatus.LibraryPath = text;
			}
			else
			{
				try
				{
					ffmpeg.RootPath = "";
					ffmpeg.av_version_info();
					fFmpegInstallationStatus.IsInstalled = true;
					fFmpegInstallationStatus.LibraryPath = "System default";
				}
				catch
				{
					fFmpegInstallationStatus.IsInstalled = false;
				}
			}
			fFmpegInstallationStatus.IsArchitectureCompatible = true;
		}
		catch (Exception ex)
		{
			fFmpegInstallationStatus.Error = ex.Message;
		}
		fFmpegInstallationStatus.InstallationInstructions = GetInstallationInstructions();
		return fFmpegInstallationStatus;
	}

	private static string? FindFFmpegPath(string? customPath)
	{
		string text = FindBundledFFmpeg();
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		string baseDirectory = AppContext.BaseDirectory;
		string[] array = new string[3]
		{
			Path.Combine(baseDirectory, "ffmpeg"),
			Path.Combine(baseDirectory, "lib"),
			baseDirectory
		};
		foreach (string text2 in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text2))
			{
				return text2;
			}
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return FindWindowsFFmpeg();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return FindMacOSFFmpeg();
		}
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return FindLinuxFFmpeg();
		}
		return null;
	}

	private static string? FindBundledFFmpeg()
	{
		return FFmpegPathResolver.TryConfigureBundledFFmpeg();
	}

	private static string? FindWindowsFFmpeg()
	{
		string[] array = new string[5]
		{
			"C:\\ffmpeg\\bin",
			"C:\\Program Files\\ffmpeg\\bin",
			"C:\\Program Files (x86)\\ffmpeg\\bin",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin")
		};
		foreach (string text in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text))
			{
				return text;
			}
		}
		string environmentVariable = Environment.GetEnvironmentVariable("PATH");
		if (!string.IsNullOrEmpty(environmentVariable))
		{
			array = environmentVariable.Split(';');
			foreach (string text2 in array)
			{
				if (FFmpegPathResolver.HasFFmpegLibrary(text2))
				{
					return text2;
				}
			}
		}
		return null;
	}

	private static string? FindMacOSFFmpeg()
	{
		string[] array = new string[4] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/homebrew/Cellar/ffmpeg", "/usr/local/Cellar/ffmpeg" };
		foreach (string text in array)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(text))
			{
				return text;
			}
			if (!text.Contains("Cellar") || !Directory.Exists(text))
			{
				continue;
			}
			try
			{
				string[] directories = Directory.GetDirectories(text);
				for (int j = 0; j < directories.Length; j++)
				{
					string text2 = Path.Combine(directories[j], "lib");
					if (FFmpegPathResolver.HasFFmpegLibrary(text2))
					{
						return text2;
					}
				}
			}
			catch
			{
			}
		}
		if (FFmpegPathResolver.HasFFmpegLibrary("/opt/local/lib"))
		{
			return "/opt/local/lib";
		}
		return null;
	}

	private static string? FindLinuxFFmpeg()
	{
		List<string> list = new List<string>();
		if (IsArm)
		{
			list.AddRange(new string[2] { "/usr/lib/aarch64-linux-gnu", "/usr/lib64" });
		}
		else
		{
			list.AddRange(new string[2] { "/usr/lib/x86_64-linux-gnu", "/usr/lib64" });
		}
		list.AddRange(new string[3] { "/usr/lib", "/usr/local/lib", "/lib" });
		foreach (string item in list)
		{
			if (FFmpegPathResolver.HasFFmpegLibrary(item))
			{
				return item;
			}
		}
		string environmentVariable = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
		if (!string.IsNullOrEmpty(environmentVariable))
		{
			string[] array = environmentVariable.Split(':');
			foreach (string text in array)
			{
				if (FFmpegPathResolver.HasFFmpegLibrary(text))
				{
					return text;
				}
			}
		}
		return null;
	}

	[Conditional("DEBUG")]
	private static void Log(string message)
	{
		Console.WriteLine("[FFmpegInitializer] " + message);
	}
}
/// <summary>
/// Exception thrown when FFmpeg is not installed or cannot be found on the system.
/// </summary>
public class FFmpegNotFoundException : Exception
{
	public FFmpegNotFoundException(string message)
		: base(message)
	{
	}

	public FFmpegNotFoundException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
/// <summary>
/// Provides information about the FFmpeg installation status on the system.
/// </summary>
public class FFmpegInstallationStatus
{
	/// <summary>
	/// The current operating system platform (windows, macos, linux).
	/// </summary>
	public string Platform { get; set; } = "";

	/// <summary>
	/// The current CPU architecture (x64, x86, arm64, arm).
	/// </summary>
	public string Architecture { get; set; } = "";

	/// <summary>
	/// Whether FFmpeg libraries were found on the system.
	/// </summary>
	public bool IsInstalled { get; set; }

	/// <summary>
	/// Whether the found FFmpeg libraries are compatible with the current architecture.
	/// </summary>
	public bool IsArchitectureCompatible { get; set; }

	/// <summary>
	/// The path to the FFmpeg libraries, if found.
	/// </summary>
	public string? LibraryPath { get; set; }

	/// <summary>
	/// Any error message encountered during detection.
	/// </summary>
	public string? Error { get; set; }

	/// <summary>
	/// Platform-specific installation instructions.
	/// </summary>
	public string InstallationInstructions { get; set; } = "";

	/// <summary>
	/// Whether FFmpeg is ready to use (installed and architecture compatible).
	/// </summary>
	public bool IsReady
	{
		get
		{
			if (IsInstalled)
			{
				return IsArchitectureCompatible;
			}
			return false;
		}
	}

	public override string ToString()
	{
		if (IsReady)
		{
			return "FFmpeg is installed and ready at: " + LibraryPath;
		}
		if (IsInstalled && !IsArchitectureCompatible)
		{
			return "FFmpeg is installed at " + LibraryPath + " but is not compatible with " + Architecture;
		}
		return "FFmpeg is not installed.\n" + InstallationInstructions;
	}
}
