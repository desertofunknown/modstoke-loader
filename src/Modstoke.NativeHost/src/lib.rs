#![allow(non_snake_case)]
#![allow(non_upper_case_globals)]
#![allow(unsafe_op_in_unsafe_fn)]

use std::arch::{global_asm, naked_asm};
use std::ffi::{CStr, c_char, c_int, c_void};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::ptr::{addr_of_mut, null, null_mut};
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicPtr, AtomicUsize, Ordering};

type Bool = i32;
type Dword = u32;
type Hinstance = *mut c_void;
type Hmodule = *mut c_void;
type Farproc = *mut c_void;

const TRUE: Bool = 1;
const FALSE: Bool = 0;
const DLL_PROCESS_DETACH: Dword = 0;
const DLL_PROCESS_ATTACH: Dword = 1;
const MAX_PATH_U32: Dword = 260;
const PAGE_READWRITE: Dword = 0x04;
const IMAGE_DIRECTORY_ENTRY_IMPORT: usize = 1;
const IMAGE_NT_SIGNATURE: u32 = 0x0000_4550;
const IMAGE_NT_OPTIONAL_HDR64_MAGIC: u16 = 0x20b;
const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;
const HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER: i32 = 5;
const UNSET_HOSTFXR_RESULT: i32 = i32::MIN;
const NATIVE_HOST_LOG_NAME: &str = "openacai-native-host.log";
const LOADER_SETTINGS_NAME: &str = "openacai-loader.settings.cfg";
const GRAPHICS_API_KEY: &str = "graphicsApi";
// Written before a start with a forced graphics API and removed by the Loader once the menus draw, so a start that
// never got that far is known on the next one.
const GRAPHICS_PENDING_NAME: &str = "graphics-api-pending";
const GRAPHICS_FAILED_NAME: &str = "graphics-api-failed";
// Launch options that already pick a graphics API, or start the game without graphics.
const GRAPHICS_FLAGS: [&str; 6] = ["-force-d3d", "-force-vulkan", "-force-gl", "-force-gfx-", "-batchmode", "-nographics"];

static COMMAND_LINE_W: OnceLock<Vec<u16>> = OnceLock::new();
static COMMAND_LINE_A: OnceLock<Vec<u8>> = OnceLock::new();

// DirectX 12 device-removed data (DRED), switched on for starts on DirectX 12.
const DRED_FORCED_ON: i32 = 2;
const IID_DRED_SETTINGS1: Guid = Guid {
    data1: 0xDBD5_AE51,
    data2: 0x3317,
    data3: 0x4F0A,
    data4: [0xAD, 0xF9, 0x1D, 0x7C, 0xED, 0xCA, 0xAE, 0x0B],
};
const IID_DRED_SETTINGS: Guid = Guid {
    data1: 0x82BC_481C,
    data2: 0x6B9B,
    data3: 0x4030,
    data4: [0xAE, 0xDB, 0x7E, 0xE3, 0xD1, 0xDF, 0x1E, 0x63],
};
const IID_D3D12_FENCE: Guid = Guid {
    data1: 0x0A75_3DCF,
    data2: 0xC4D8,
    data3: 0x4B91,
    data4: [0xAD, 0xF6, 0xBE, 0x5A, 0x60, 0xD9, 0x5A, 0x76],
};
const IID_DRED: Guid = Guid {
    data1: 0x9893_1D33,
    data2: 0x5AE8,
    data3: 0x4791,
    data4: [0xAA, 0x3C, 0x1A, 0x73, 0xA2, 0x93, 0x4E, 0x71],
};
const IID_DRED1: Guid = Guid {
    data1: 0x9727_A022,
    data2: 0xCF1D,
    data3: 0x4DDA,
    data4: [0x9E, 0xBA, 0xEF, 0xFA, 0x65, 0x3F, 0xC5, 0x06],
};
const DEVICE_REMOVED_REPORT_PREFIX: &str = "dx12-device-removed-";
static DRED_WANTED: AtomicBool = AtomicBool::new(false);
static DRED_REQUESTED: AtomicBool = AtomicBool::new(false);
static DEVICE_WATCHED: AtomicBool = AtomicBool::new(false);
static ORIGINAL_D3D12_CREATE_DEVICE: AtomicPtr<c_void> = AtomicPtr::new(null_mut());

#[repr(C)]
struct Guid {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [u8; 8],
}

type D3d12CreateDevice =
    unsafe extern "system" fn(adapter: *mut c_void, level: i32, riid: *const Guid, device: *mut *mut c_void) -> i32;
type D3d12GetDebugInterface = unsafe extern "system" fn(riid: *const Guid, debug: *mut *mut c_void) -> i32;
type ComSetEnablement = unsafe extern "system" fn(this: *mut c_void, enablement: i32);
type ComRelease = unsafe extern "system" fn(this: *mut c_void) -> u32;
type ComQueryInterface = unsafe extern "system" fn(this: *mut c_void, riid: *const Guid, out: *mut *mut c_void) -> i32;
type DeviceCreateFence =
    unsafe extern "system" fn(this: *mut c_void, initial: u64, flags: u32, riid: *const Guid, fence: *mut *mut c_void) -> i32;
type DeviceRemovedReason = unsafe extern "system" fn(this: *mut c_void) -> i32;
type FenceSetEventOnCompletion = unsafe extern "system" fn(this: *mut c_void, value: u64, event: *mut c_void) -> i32;
type DredGetOutput = unsafe extern "system" fn(this: *mut c_void, output: *mut c_void) -> i32;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateEventW(attributes: *mut c_void, manual_reset: Bool, initial_state: Bool, name: *const u16) -> *mut c_void;
    fn WaitForSingleObject(handle: *mut c_void, milliseconds: Dword) -> Dword;
}

struct HostState {
    game_dir: OnceLock<PathBuf>,
    log_path: OnceLock<PathBuf>,
    state_path: OnceLock<PathBuf>,
    config: OnceLock<NativeHostConfig>,
    bootstrap_started: AtomicBool,
    bootstrap_thread: AtomicUsize,
    original_il2cpp_init: AtomicPtr<c_void>,
    native_host_path: OnceLock<PathBuf>,
    proxy_target_path: OnceLock<PathBuf>,
    dotnet_root_path: OnceLock<PathBuf>,
    target_directory: OnceLock<PathBuf>,
    managed_folder_directory: OnceLock<PathBuf>,
    entrypoint_type_name: OnceLock<String>,
    app_paths_value: OnceLock<String>,
    captured_function_name: OnceLock<String>,
    proxy_resolved_export_count: AtomicUsize,
    proxy_missing_export_count: AtomicUsize,
    hostfxr_machine: AtomicUsize,
    hostfxr_optional_magic: AtomicUsize,
    coreclr_machine: AtomicUsize,
    coreclr_optional_magic: AtomicUsize,
    iat_hooked_unity_player: AtomicBool,
    iat_hooked_executable: AtomicBool,
    hostfxr_module_loaded: AtomicBool,
    hostfxr_error_writer_set: AtomicBool,
    hostfxr_initialize_result: AtomicI32,
    hostfxr_delegate_result: AtomicI32,
    load_assembly_result: AtomicI32,
}

impl HostState {
    const fn new() -> Self {
        Self {
            game_dir: OnceLock::new(),
            log_path: OnceLock::new(),
            state_path: OnceLock::new(),
            config: OnceLock::new(),
            bootstrap_started: AtomicBool::new(false),
            bootstrap_thread: AtomicUsize::new(0),
            original_il2cpp_init: AtomicPtr::new(null_mut()),
            native_host_path: OnceLock::new(),
            proxy_target_path: OnceLock::new(),
            dotnet_root_path: OnceLock::new(),
            target_directory: OnceLock::new(),
            managed_folder_directory: OnceLock::new(),
            entrypoint_type_name: OnceLock::new(),
            app_paths_value: OnceLock::new(),
            captured_function_name: OnceLock::new(),
            proxy_resolved_export_count: AtomicUsize::new(0),
            proxy_missing_export_count: AtomicUsize::new(0),
            hostfxr_machine: AtomicUsize::new(0),
            hostfxr_optional_magic: AtomicUsize::new(0),
            coreclr_machine: AtomicUsize::new(0),
            coreclr_optional_magic: AtomicUsize::new(0),
            iat_hooked_unity_player: AtomicBool::new(false),
            iat_hooked_executable: AtomicBool::new(false),
            hostfxr_module_loaded: AtomicBool::new(false),
            hostfxr_error_writer_set: AtomicBool::new(false),
            hostfxr_initialize_result: AtomicI32::new(UNSET_HOSTFXR_RESULT),
            hostfxr_delegate_result: AtomicI32::new(UNSET_HOSTFXR_RESULT),
            load_assembly_result: AtomicI32::new(UNSET_HOSTFXR_RESULT),
        }
    }
}

static STATE: HostState = HostState::new();

#[derive(Debug)]
enum BootstrapError {
    MissingExport(String),
    ConfigParseFailed(PathBuf, String),
    MissingFile(String, PathBuf),
    InvalidPeImage(String, PathBuf),
    InvalidPeHeader(String, PathBuf),
    InvalidPeSignature(String, PathBuf),
    ArchitectureMismatch(String, PathBuf),
    IatHookFailed(String),
    HostfxrInitFailed(i32),
    HostfxrDelegateFailed(i32),
    LoadAssemblyFailed(i32),
    Generic(String),
}

impl std::fmt::Display for BootstrapError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::MissingExport(s) => write!(f, "Missing native export: {s}"),
            Self::ConfigParseFailed(p, e) => write!(f, "Failed to read {}: {e}", p.display()),
            Self::MissingFile(l, p) => write!(f, "{l} does not exist: {}", p.display()),
            Self::InvalidPeImage(l, p) => write!(f, "{l} is not a valid PE image: {}", p.display()),
            Self::InvalidPeHeader(l, p) => write!(f, "{l} has an invalid PE header offset: {}", p.display()),
            Self::InvalidPeSignature(l, p) => write!(f, "{l} has an invalid NT signature: {}", p.display()),
            Self::ArchitectureMismatch(l, p) => write!(f, "{l} architecture does not match the x64 game process: {}", p.display()),
            Self::IatHookFailed(s) => write!(f, "Failed to install GetProcAddress IAT hook: {s}"),
            Self::HostfxrInitFailed(code) => write!(f, "hostfxr_initialize_for_runtime_config failed: 0x{code:08x}"),
            Self::HostfxrDelegateFailed(code) => write!(f, "hostfxr_get_runtime_delegate failed: 0x{code:08x}"),
            Self::LoadAssemblyFailed(code) => write!(f, "load_assembly_and_get_function_pointer failed for Doorstop.Entrypoint.Start: 0x{code:08x}"),
            Self::Generic(s) => write!(f, "{s}"),
        }
    }
}
impl std::error::Error for BootstrapError {}

impl From<String> for BootstrapError {
    fn from(s: String) -> Self {
        Self::Generic(s)
    }
}



#[repr(C)]
struct HostfxrInitializeParameters {
    size: usize,
    host_path: *const u16,
    dotnet_root: *const u16,
}

type HostfxrHandle = *mut c_void;
type HostfxrInitializeForRuntimeConfig = unsafe extern "C" fn(
    runtime_config_path: *const u16,
    parameters: *const HostfxrInitializeParameters,
    host_context_handle: *mut HostfxrHandle,
) -> c_int;
type HostfxrGetRuntimeDelegate = unsafe extern "C" fn(
    host_context_handle: HostfxrHandle,
    delegate_type: c_int,
    delegate: *mut *mut c_void,
) -> c_int;
type HostfxrClose = unsafe extern "C" fn(host_context_handle: HostfxrHandle) -> c_int;
type HostfxrErrorWriter = unsafe extern "C" fn(message: *const u16);
type HostfxrSetErrorWriter =
    unsafe extern "C" fn(error_writer: HostfxrErrorWriter) -> HostfxrErrorWriter;
type LoadAssemblyAndGetFunctionPointer = unsafe extern "system" fn(
    assembly_path: *const u16,
    type_name: *const u16,
    method_name: *const u16,
    delegate_type_name: *const u16,
    reserved: *mut c_void,
    delegate: *mut *mut c_void,
) -> c_int;
type ManagedAction = unsafe extern "system" fn();
type Il2CppInit = unsafe extern "system" fn(domain_name: *const c_char) -> c_int;
#[derive(Clone)]
struct NativeHostConfig {
    enabled: bool,
    ignore_disable_switch: bool,
    redirect_output_log: bool,
    target_assembly: PathBuf,
    coreclr_path: Option<PathBuf>,
    corlib_dir: Option<PathBuf>,
    hostfxr_path: PathBuf,
    runtime_config_path: PathBuf,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetCommandLineW() -> *const u16;
    fn GetCommandLineA() -> *const c_char;
    fn GetModuleFileNameW(module: Hmodule, filename: *mut u16, size: Dword) -> Dword;
    fn GetModuleHandleW(module_name: *const u16) -> Hmodule;
    fn GetCurrentThreadId() -> Dword;
    fn GetProcAddress(module: Hmodule, proc_name: *const c_char) -> Farproc;
    fn LoadLibraryW(file_name: *const u16) -> Hmodule;
    fn GetSystemDirectoryW(buffer: *mut u16, size: Dword) -> Dword;
    fn SetCurrentDirectoryW(path_name: *const u16) -> Bool;
    fn SetEnvironmentVariableW(name: *const u16, value: *const u16) -> Bool;
    fn VirtualProtect(
        address: *mut c_void,
        size: usize,
        new_protect: Dword,
        old_protect: *mut Dword,
    ) -> Bool;
}

// The CRT needs a linkable DllMain, not a public DLL export. no_mangle would
// add it to Rust's explicit export list, bypassing the linker's autoexport filter.
global_asm!(
    ".text",
    ".p2align 4",
    ".globl DllMain",
    "DllMain:",
    "jmp {entry}",
    entry = sym native_dll_main,
);

unsafe extern "system" fn native_dll_main(
    hinst_dll: Hinstance,
    reason_for_call: Dword,
    _reserved: *mut c_void,
) -> Bool {
    if reason_for_call == DLL_PROCESS_DETACH {
        clear_env("DOORSTOP_DISABLE");
        return TRUE;
    }

    if reason_for_call != DLL_PROCESS_ATTACH {
        return TRUE;
    }

    if let Err(detail) = process_attach(hinst_dll) {
        log_line(&format!("Attach failed: {detail}"));
        write_state("FAILED", &detail.to_string(), Some("AttachFailure"));
    }

    TRUE
}

unsafe fn process_attach(hinst_dll: Hinstance) -> Result<(), BootstrapError> {
    let game_exe = module_path(null_mut())?;
    let game_dir = game_exe
        .parent()
        .ok_or_else(|| BootstrapError::Generic(format!("Game executable path has no parent: {}", game_exe.display())))?
        .to_path_buf();
    let _ = STATE.game_dir.set(game_dir.clone());

    // The log and state files live next to the configured bootstrap so they follow whichever runtime folder is in use.
    let config_path = game_dir.join("doorstop_config.ini");
    let config = load_config(&config_path, &game_dir);
    let runtime_dir = config
        .as_ref()
        .ok()
        .and_then(|config| config.target_assembly.parent())
        .filter(|dir| dir.is_dir())
        .map(Path::to_path_buf)
        .unwrap_or_else(|| default_runtime_dir(&game_dir));
    let _ = STATE.log_path.set(runtime_dir.join(NATIVE_HOST_LOG_NAME));
    let _ = STATE.state_path.set(runtime_dir.join("openacai-native-host-state.json"));

    log_line("Modstoke Native Host attached");
    log_line(&format!("Executable path: {}", game_exe.display()));
    log_line(&format!("Game directory: {}", game_dir.display()));
    log_line(&format!("Runtime directory: {}", runtime_dir.display()));

    let cwd_w = wide_path(&game_dir);
    SetCurrentDirectoryW(cwd_w.as_ptr());

    let self_path = module_path(hinst_dll)?;
    let _ = STATE.native_host_path.set(self_path.clone());
    log_line(&format!("Native host path: {}", self_path.display()));
    load_proxy_exports(null_mut());
    load_proxy(&self_path, &game_dir);

    let config = config?;
    log_line(&format!("Config loaded from {}", config_path.display()));
    log_line(&format!(
        "Config target assembly: {}",
        config.target_assembly.display()
    ));
    log_line(&format!(
        "Config hostfxr: {}",
        config.hostfxr_path.display()
    ));
    log_line(&format!(
        "Config runtimeconfig: {}",
        config.runtime_config_path.display()
    ));
    log_line(&format!(
        "Config redirect_output_log: {}",
        config.redirect_output_log
    ));

    let disabled_env = std::env::var_os("DOORSTOP_DISABLE").is_some();
    if !config.ignore_disable_switch && disabled_env {
        log_line("DOORSTOP_DISABLE is set; native host disabled");
        let _ = STATE.config.set(config);
        write_state(
            "DISABLED",
            "DOORSTOP_DISABLE was set before native host initialization.",
            None,
        );
        return Ok(());
    }

    if !config.enabled {
        log_line("Native host disabled by config");
        let _ = STATE.config.set(config);
        write_state(
            "DISABLED",
            "doorstop_config.ini disabled the native host.",
            None,
        );
        return Ok(());
    }

    validate_bootstrap_config(&config)?;
    let _ = STATE.config.set(config);

    apply_graphics_api(&game_exe, &game_dir);
    let command_line = match COMMAND_LINE_W.get() {
        Some(line) => String::from_utf16_lossy(&line[..line.len().saturating_sub(1)]),
        None => String::from_utf16_lossy(wide_slice(GetCommandLineW())),
    };
    DRED_WANTED.store(command_line.to_ascii_lowercase().contains("-force-d3d12"), Ordering::SeqCst);
    install_get_proc_address_hook()?;
    set_env("DOORSTOP_DISABLE", "TRUE");
    write_state(
        "HOOKED",
        "Modstoke Native Host installed the IL2CPP bootstrap hook.",
        None,
    );
    Ok(())
}

/// Adds the graphics API chosen in Modstoke's options to the command line Unity reads. Unity picks its graphics
/// device from -force-d3d11 or -force-d3d12 on the command line only, before any managed code runs, so the choice
/// is applied here, at the next start after it was made.
unsafe fn apply_graphics_api(game_exe: &Path, game_dir: &Path) {
    let user_data = user_data_dir(game_dir);
    let Some(choice) = read_setting(&user_data.join(LOADER_SETTINGS_NAME), GRAPHICS_API_KEY) else {
        return;
    };
    let flag = match choice.to_ascii_lowercase().as_str() {
        "d3d12" => "-force-d3d12",
        "d3d11" => "-force-d3d11",
        _ => return,
    };

    let server = game_exe
        .file_stem()
        .and_then(|stem| stem.to_str())
        .is_some_and(|stem| stem.to_ascii_lowercase().ends_with("ds"));
    let original_w = GetCommandLineW();
    let original_a = GetCommandLineA();
    if server || original_w.is_null() || original_a.is_null() {
        return;
    }
    let line = String::from_utf16_lossy(wide_slice(original_w));
    let lower = line.to_ascii_lowercase();
    if GRAPHICS_FLAGS.iter().any(|known| lower.contains(known)) {
        log_line("Graphics API: the launch options already choose one; the Modstoke setting is left out");
        return;
    }

    let pending = user_data.join(GRAPHICS_PENDING_NAME);
    if pending.is_file() {
        // The last start with this choice never reached the menus; this one starts as the game would.
        let _ = fs::rename(&pending, user_data.join(GRAPHICS_FAILED_NAME));
        log_line(&format!("Graphics API: the last start with {flag} did not finish; starting without it"));
        return;
    }
    if fs::write(&pending, flag).is_err() {
        log_line("Graphics API: the start marker could not be written; starting without the setting");
        return;
    }

    let _ = COMMAND_LINE_W.set(wide_null(&format!("{line} {flag}")));
    let mut ansi = CStr::from_ptr(original_a).to_bytes().to_vec();
    ansi.extend_from_slice(b" ");
    ansi.extend_from_slice(flag.as_bytes());
    ansi.push(0);
    let _ = COMMAND_LINE_A.set(ansi);

    let kernel32 = GetModuleHandleW(wide_null("kernel32.dll").as_ptr());
    let exe = GetModuleHandleW(null());
    let unity = GetModuleHandleW(wide_null("UnityPlayer.dll").as_ptr());
    let mut hooked = false;
    for (name, detour) in [
        (&b"GetCommandLineW\0"[..], get_command_line_w_detour as Farproc),
        (&b"GetCommandLineA\0"[..], get_command_line_a_detour as Farproc),
    ] {
        let target = GetProcAddress(kernel32, name.as_ptr().cast());
        if target.is_null() {
            continue;
        }
        for module in [unity, exe] {
            if !module.is_null() {
                hooked |= iat_hook(module, None, target, detour);
            }
        }
    }
    if hooked {
        log_line(&format!("Graphics API: starting with {flag}"));
    } else {
        let _ = fs::remove_file(&pending);
        log_line("Graphics API: the command line could not be hooked; starting without the setting");
    }
}

unsafe extern "system" fn get_command_line_w_detour() -> *const u16 {
    COMMAND_LINE_W.get().map_or_else(|| GetCommandLineW(), |line| line.as_ptr())
}

unsafe extern "system" fn get_command_line_a_detour() -> *const c_char {
    COMMAND_LINE_A.get().map_or_else(|| GetCommandLineA(), |line| line.as_ptr().cast())
}

unsafe fn wide_slice<'a>(text: *const u16) -> &'a [u16] {
    let mut length = 0;
    while *text.add(length) != 0 {
        length += 1;
    }
    std::slice::from_raw_parts(text, length)
}

fn user_data_dir(game_dir: &Path) -> PathBuf {
    let user_data = game_dir.join("Modstoke").join("UserData");
    let legacy = game_dir.join("OpenACAI").join("UserData");
    if !user_data.is_dir() && legacy.is_dir() { legacy } else { user_data }
}

/// One value from the Loader's settings file, which holds `key = value` lines.
fn read_setting(path: &Path, key: &str) -> Option<String> {
    let text = fs::read_to_string(path).ok()?;
    text.lines()
        .map(str::trim)
        .filter(|line| !line.starts_with('#') && !line.starts_with(';'))
        .filter_map(|line| line.split_once('='))
        .find(|(name, _)| name.trim().eq_ignore_ascii_case(key))
        .map(|(_, value)| value.trim().to_string())
}

unsafe fn install_get_proc_address_hook() -> Result<(), BootstrapError> {
    let kernel32 = GetModuleHandleW(wide_null("kernel32.dll").as_ptr());
    if kernel32.is_null() {
        return Err(BootstrapError::Generic("kernel32.dll was not loaded.".to_string()));
    }

    let target = GetProcAddress(kernel32, b"GetProcAddress\0".as_ptr().cast());
    if target.is_null() {
        return Err(BootstrapError::Generic("kernel32!GetProcAddress could not be resolved.".to_string()));
    }

    let exe = GetModuleHandleW(null());
    let unity = GetModuleHandleW(wide_null("UnityPlayer.dll").as_ptr());
    let hook = get_proc_address_detour as *mut c_void;
    let mut hooked = false;

    if !unity.is_null() {
        let unity_hooked = iat_hook(unity, Some("kernel32.dll"), target, hook);
        STATE.iat_hooked_unity_player.store(unity_hooked, Ordering::SeqCst);
        hooked |= unity_hooked;
    }

    if !exe.is_null() && exe != unity {
        let exe_hooked = iat_hook(exe, Some("kernel32.dll"), target, hook);
        STATE.iat_hooked_executable.store(exe_hooked, Ordering::SeqCst);
        hooked |= exe_hooked;
    }

    if !hooked {
        return Err(BootstrapError::IatHookFailed("Missing modules or hooks failed.".to_string()));
    }

    log_line("Installed GetProcAddress IAT hook");
    Ok(())
}

unsafe extern "system" fn get_proc_address_detour(module: Hmodule, name: *const c_char) -> Farproc {
    if !name.is_null() && !is_ordinal(name) {
        let requested = CStr::from_ptr(name).to_bytes();
        if requested.eq_ignore_ascii_case(b"il2cpp_init") {
            let original = GetProcAddress(module, name);
            if !original.is_null() {
                STATE.original_il2cpp_init.store(original, Ordering::SeqCst);
                let _ = STATE.captured_function_name.set("il2cpp_init".to_string());
                log_line("Captured il2cpp_init and returned OpenACAI bootstrap detour");
                return openacai_il2cpp_init as Farproc;
            }
        }
        if requested == b"D3D12CreateDevice" && DRED_WANTED.load(Ordering::SeqCst) {
            let original = GetProcAddress(module, name);
            if !original.is_null() {
                ORIGINAL_D3D12_CREATE_DEVICE.store(original, Ordering::SeqCst);
                return d3d12_create_device_detour as Farproc;
            }
        }
    }

    GetProcAddress(module, name)
}

/// The engine loads d3d12.dll itself and creates its device through this export, so DRED can be switched on just
/// before, outside the loader lock.
unsafe extern "system" fn d3d12_create_device_detour(
    adapter: *mut c_void,
    level: i32,
    riid: *const Guid,
    device: *mut *mut c_void,
) -> i32 {
    if !DRED_REQUESTED.swap(true, Ordering::SeqCst) {
        enable_dred();
    }
    let create: D3d12CreateDevice = std::mem::transmute(ORIGINAL_D3D12_CREATE_DEVICE.load(Ordering::SeqCst));
    let result = create(adapter, level, riid, device);
    // The engine first asks whether a device could be created, without one; the real one comes back here.
    if result >= 0 && !device.is_null() && !(*device).is_null() && !DEVICE_WATCHED.swap(true, Ordering::SeqCst) {
        watch_device_removal(*device);
    }
    result
}

/// Every fence of a removed device is signalled to the largest value, so a fence nobody else signals gives a wait
/// that ends exactly when the device is lost. The thread then writes what DRED recorded, before the engine fails
/// its next allocation and crashes.
unsafe fn watch_device_removal(device: *mut c_void) {
    let vtable = *(device as *const *const usize);
    let create_fence: DeviceCreateFence = std::mem::transmute(*vtable.add(36));
    let mut fence = null_mut();
    if create_fence(device, 0, 0, &IID_D3D12_FENCE, &mut fence) < 0 || fence.is_null() {
        log_line("DRED: no fence for the device-removed watch; a removal will only be in the engine's log");
        return;
    }
    let event = CreateEventW(null_mut(), TRUE, FALSE, null());
    let set_event: FenceSetEventOnCompletion = std::mem::transmute(*(*(fence as *const *const usize)).add(9));
    if event.is_null() || set_event(fence, u64::MAX, event) < 0 {
        log_line("DRED: the device-removed watch could not be armed");
        return;
    }
    let (device, event) = (device as usize, event as usize);
    let _ = std::thread::Builder::new().name("Modstoke DX12 watch".into()).spawn(move || {
        WaitForSingleObject(event as *mut c_void, u32::MAX);
        write_device_removed_report(device as *mut c_void);
    });
    log_line("DRED: watching the DirectX 12 device for removal");
}

unsafe fn write_device_removed_report(device: *mut c_void) {
    let vtable = *(device as *const *const usize);
    let reason: DeviceRemovedReason = std::mem::transmute(*vtable.add(37));
    let reason = reason(device) as u32;
    let mut report = format!("DirectX 12 device removed: 0x{reason:08X} ({})\n", removal_reason_name(reason));
    let query: ComQueryInterface = std::mem::transmute(*vtable.add(0));
    let mut dred = null_mut();
    let version = if query(device, &IID_DRED1, &mut dred) >= 0 && !dred.is_null() {
        1
    } else if query(device, &IID_DRED, &mut dred) >= 0 && !dred.is_null() {
        0
    } else {
        report.push_str("No DRED data: the device did not expose it.\n");
        write_report(&report);
        return;
    };
    let dred_vtable = *(dred as *const *const usize);
    let mut breadcrumbs: usize = 0;
    let get_breadcrumbs: DredGetOutput = std::mem::transmute(*dred_vtable.add(if version == 1 { 5 } else { 3 }));
    if get_breadcrumbs(dred, (&mut breadcrumbs as *mut usize).cast()) >= 0 {
        append_breadcrumbs(&mut report, breadcrumbs as *const u8, version == 1);
    } else {
        report.push_str("Breadcrumbs: unavailable\n");
    }
    let mut fault = [0usize; 3];
    let get_fault: DredGetOutput = std::mem::transmute(*dred_vtable.add(if version == 1 { 6 } else { 4 }));
    if get_fault(dred, fault.as_mut_ptr().cast()) >= 0 && fault[0] != 0 {
        report.push_str(&format!("\nPage fault at GPU address 0x{:016X}\n", fault[0]));
        append_allocations(&mut report, "Resources there", fault[1] as *const u8, version == 1);
        append_allocations(&mut report, "Recently freed there", fault[2] as *const u8, version == 1);
    } else {
        report.push_str("\nNo page fault recorded.\n");
    }
    write_report(&report);
}

// D3D12_AUTO_BREADCRUMB_NODE(1): names at 0..32, BreadcrumbCount 48, pLastBreadcrumbValue 56, pCommandHistory 64,
// pNext 72; NODE1 adds BreadcrumbContextsCount 80 and pBreadcrumbContexts 88 (index, wide string).
unsafe fn append_breadcrumbs(report: &mut String, mut node: *const u8, with_contexts: bool) {
    report.push_str("Command lists the GPU had not finished (finished lists are left out):\n");
    let mut shown = 0;
    while !node.is_null() && shown < 64 {
        let count = *(node.add(48) as *const u32);
        let last_ptr = *(node.add(56) as *const *const u32);
        let last = if last_ptr.is_null() { 0 } else { *last_ptr };
        if last < count {
            shown += 1;
            let list = node_name(node, 0, 8);
            let queue = node_name(node, 16, 24);
            report.push_str(&format!("\n  queue \"{queue}\", list \"{list}\": {last} of {count} operations done\n"));
            let history = *(node.add(64) as *const *const u32);
            let (contexts, context_count) = if with_contexts {
                (*(node.add(88) as *const *const u8), *(node.add(80) as *const u32))
            } else {
                (null(), 0)
            };
            let first = last.saturating_sub(12);
            let end = (last + 6).min(count);
            for index in first..end {
                let op = if history.is_null() { u32::MAX } else { *history.add(index as usize) };
                let marker = if index == last { "  <- running when the GPU stopped" } else { "" };
                report.push_str(&format!("    #{index:<6} {}{marker}\n", breadcrumb_op_name(op)));
                for context in 0..context_count as usize {
                    let entry = contexts.add(context * 16);
                    if *(entry as *const u32) == index {
                        report.push_str(&format!("            {}\n", wide_text(*(entry.add(8) as *const *const u16))));
                    }
                }
            }
        }
        node = *(node.add(72) as *const *const u8);
    }
    if shown == 0 {
        report.push_str("  none: every recorded command list had finished\n");
    }
}

// D3D12_DRED_ALLOCATION_NODE(1): ObjectNameA 0, ObjectNameW 8, AllocationType 16, pNext 24.
unsafe fn append_allocations(report: &mut String, label: &str, mut node: *const u8, _with_object: bool) {
    report.push_str(&format!("  {label}:\n"));
    let mut count = 0;
    while !node.is_null() && count < 32 {
        let name = node_name(node, 0, 8);
        let kind = *(node.add(16) as *const u32);
        report.push_str(&format!("    {} (allocation type {kind})\n", if name.is_empty() { "unnamed" } else { &name }));
        node = *(node.add(24) as *const *const u8);
        count += 1;
    }
    if count == 0 {
        report.push_str("    none\n");
    }
}

unsafe fn node_name(node: *const u8, ansi: usize, wide: usize) -> String {
    let wide_name = *(node.add(wide) as *const *const u16);
    if !wide_name.is_null() {
        return wide_text(wide_name);
    }
    let ansi_name = *(node.add(ansi) as *const *const c_char);
    if ansi_name.is_null() { String::new() } else { CStr::from_ptr(ansi_name).to_string_lossy().into_owned() }
}

unsafe fn wide_text(text: *const u16) -> String {
    if text.is_null() { String::new() } else { String::from_utf16_lossy(wide_slice(text)) }
}

fn removal_reason_name(reason: u32) -> &'static str {
    match reason {
        0x887A_0005 => "DEVICE_REMOVED",
        0x887A_0006 => "DEVICE_HUNG: the GPU took too long on submitted work",
        0x887A_0007 => "DEVICE_RESET: a badly formed command or the driver reset the GPU",
        0x887A_0020 => "DRIVER_INTERNAL_ERROR",
        0x8007_000E => "OUT_OF_MEMORY",
        0 => "not removed",
        _ => "other",
    }
}

fn breadcrumb_op_name(op: u32) -> &'static str {
    const NAMES: [&str; 49] = [
        "SetMarker", "BeginEvent", "EndEvent", "DrawInstanced", "DrawIndexedInstanced", "ExecuteIndirect", "Dispatch",
        "CopyBufferRegion", "CopyTextureRegion", "CopyResource", "CopyTiles", "ResolveSubresource",
        "ClearRenderTargetView", "ClearUnorderedAccessView", "ClearDepthStencilView", "ResourceBarrier",
        "ExecuteBundle", "Present", "ResolveQueryData", "BeginSubmission", "EndSubmission", "DecodeFrame",
        "ProcessFrames", "AtomicCopyBufferUint", "AtomicCopyBufferUint64", "ResolveSubresourceRegion",
        "WriteBufferImmediate", "DecodeFrame1", "SetProtectedResourceSession", "DecodeFrame2", "ProcessFrames1",
        "BuildRaytracingAccelerationStructure", "EmitRaytracingAccelerationStructurePostbuildInfo",
        "CopyRaytracingAccelerationStructure", "DispatchRays", "InitializeMetaCommand", "ExecuteMetaCommand",
        "EstimateMotion", "ResolveMotionVectorHeap", "SetPipelineState1", "InitializeExtensionCommand",
        "ExecuteExtensionCommand", "DispatchMesh", "EncodeFrame", "ResolveEncoderOutputMetadata", "Barrier",
        "BeginCommandList", "DispatchGraph", "SetProgram",
    ];
    NAMES.get(op as usize).copied().unwrap_or("unknown")
}

unsafe fn write_report(report: &str) {
    let Some(game_dir) = STATE.game_dir.get() else {
        return;
    };
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_secs());
    let path = user_data_dir(game_dir).join(format!("{DEVICE_REMOVED_REPORT_PREFIX}{stamp}.txt"));
    match fs::write(&path, report) {
        Ok(()) => log_line(&format!("DRED: device removed; report written to {}", path.display())),
        Err(error) => log_line(&format!("DRED: device removed; the report could not be written: {error}")),
    }
}

/// Switches on DirectX 12's device-removed extended data (DRED) before the engine creates its device. Breadcrumbs
/// record how far the GPU got through each command list, and page-fault tracking names the resource behind a bad
/// GPU address. Unity prints the breadcrumbs when its device is removed, but switches DRED on only with its debug
/// layer, so the two DirectX 12 starts that hung the GPU on world activation left no trace of the work that hung it.
unsafe fn enable_dred() {
    let d3d12 = GetModuleHandleW(wide_null("d3d12.dll").as_ptr());
    let get = if d3d12.is_null() {
        null_mut()
    } else {
        GetProcAddress(d3d12, b"D3D12GetDebugInterface\0".as_ptr().cast())
    };
    if get.is_null() {
        log_line("DRED: D3D12GetDebugInterface was not found; DirectX 12 starts without device-removed data");
        return;
    }
    let get: D3d12GetDebugInterface = std::mem::transmute(get);
    for (iid, with_contexts) in [(&IID_DRED_SETTINGS1, true), (&IID_DRED_SETTINGS, false)] {
        let mut settings = null_mut();
        if get(iid, &mut settings) < 0 || settings.is_null() {
            continue;
        }
        // ID3D12DeviceRemovedExtendedDataSettings: 3 auto breadcrumbs, 4 page faults; Settings1 adds 6 contexts.
        set_dred_enablement(settings, 3);
        set_dred_enablement(settings, 4);
        if with_contexts {
            set_dred_enablement(settings, 6);
        }
        let release: ComRelease = std::mem::transmute(*(*(settings as *const *const usize)).add(2));
        release(settings);
        log_line(if with_contexts {
            "DRED: breadcrumbs, breadcrumb contexts and page-fault tracking are on for this DirectX 12 start"
        } else {
            "DRED: breadcrumbs and page-fault tracking are on for this DirectX 12 start"
        });
        return;
    }
    log_line("DRED: the settings interface was not available; DirectX 12 starts without device-removed data");
}

unsafe fn set_dred_enablement(settings: *mut c_void, slot: usize) {
    let set: ComSetEnablement = std::mem::transmute(*(*(settings as *const *const usize)).add(slot));
    set(settings, DRED_FORCED_ON);
}

unsafe extern "system" fn openacai_il2cpp_init(domain_name: *const c_char) -> c_int {
    let original = STATE.original_il2cpp_init.load(Ordering::SeqCst);
    let result = if original.is_null() {
        log_line("Original il2cpp_init was missing when detour ran");
        0
    } else {
        let original_init: Il2CppInit = std::mem::transmute(original);
        original_init(domain_name)
    };

    if !STATE.bootstrap_started.swap(true, Ordering::SeqCst) {
        // Expose only the synchronous post-init interval on the original native thread.
        let _window = BootstrapWindow::enter(result != 0);
        if let Err(detail) = hostfxr_bootstrap() {
            log_line(&format!("hostfxr bootstrap failed: {detail}"));
            write_state("FAILED", &detail.to_string(), Some("HostfxrBootstrapFailure"));
        }
    }

    result
}

struct BootstrapWindow;

impl BootstrapWindow {
    unsafe fn enter(initialized: bool) -> Self {
        if initialized {
            STATE.bootstrap_thread.store(GetCurrentThreadId() as usize, Ordering::Release);
        }
        Self
    }
}

impl Drop for BootstrapWindow {
    fn drop(&mut self) {
        STATE.bootstrap_thread.store(0, Ordering::Release);
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn OpenACAI_IsIl2CppBootstrapWindow(abi: u32) -> Bool {
    if abi == 1 && STATE.bootstrap_thread.load(Ordering::Acquire) == GetCurrentThreadId() as usize {
        TRUE
    } else {
        FALSE
    }
}

unsafe fn hostfxr_bootstrap() -> Result<(), BootstrapError> {
    let config = STATE.config
        .get()
        .ok_or_else(|| BootstrapError::Generic("Native host config was not initialized.".to_string()))?;
    let game_exe = module_path(null_mut())?;
    let target_dir = config
        .target_assembly
        .parent()
        .ok_or_else(|| {
            format!(
                "Target assembly path has no parent: {}",
                config.target_assembly.display()
            )
        })?
        .to_path_buf();
    let dotnet_root = derive_dotnet_root(&config.hostfxr_path)?;
    let target_name = config
        .target_assembly
        .file_stem()
        .and_then(|value| value.to_str())
        .ok_or_else(|| {
            format!(
                "Target assembly has no valid file stem: {}",
                config.target_assembly.display()
            )
        })?;
    let entrypoint_type = format!("Doorstop.Entrypoint, {target_name}");
    let app_paths = if let Some(corlib_dir) = &config.corlib_dir {
        if corlib_dir.is_dir() {
            format!("{};{}", corlib_dir.display(), target_dir.display())
        } else {
            target_dir.display().to_string()
        }
    } else {
        target_dir.display().to_string()
    };
    let managed_folder = config
        .corlib_dir
        .as_ref()
        .filter(|path| path.is_dir())
        .unwrap_or(&target_dir);
    let _ = STATE.dotnet_root_path.set(dotnet_root.clone());
    let _ = STATE.target_directory.set(target_dir.clone());
    let _ = STATE.entrypoint_type_name.set(entrypoint_type.clone());
    let _ = STATE.app_paths_value.set(app_paths.clone());
    let _ = STATE.managed_folder_directory.set(managed_folder.to_path_buf());

    log_line("Starting hostfxr bootstrap");
    log_line(&format!("App path: {}", game_exe.display()));
    log_line(&format!("dotnet root: {}", dotnet_root.display()));
    log_line(&format!("Target dir: {}", target_dir.display()));
    log_line(&format!("Entrypoint type: {entrypoint_type}"));
    log_line(&format!("APP_PATHS: {app_paths}"));

    set_env("DOORSTOP_INITIALIZED", "TRUE");
    set_env_path("DOORSTOP_INVOKE_DLL_PATH", &config.target_assembly);
    set_env_path("DOORSTOP_PROCESS_PATH", &game_exe);
    set_env_path("DOORSTOP_MANAGED_FOLDER_DIR", managed_folder);
    set_env("DOORSTOP_DLL_SEARCH_DIRS", &app_paths);

    // Wine backs .NET's write-xor-execute memory with a file in /tmp, and game hosts cap /tmp
    // (Wings gives a server 100 MB). A modded server compiles more code than that and dies
    // with "Internal CLR error", so under Wine compiled code goes in ordinary memory.
    if running_under_wine() && std::env::var_os("DOTNET_EnableWriteXorExecute").is_none() {
        set_env("DOTNET_EnableWriteXorExecute", "0");
        log_line("Running under Wine: DOTNET_EnableWriteXorExecute=0");
    }

    let hostfxr_w = wide_path(&config.hostfxr_path);
    let hostfxr_module = LoadLibraryW(hostfxr_w.as_ptr());
    if hostfxr_module.is_null() {
        return Err(format!(
            "Failed to load hostfxr.dll: {}",
            config.hostfxr_path.display()
        ).into());
    }
    STATE.hostfxr_module_loaded.store(true, Ordering::SeqCst);

    let initialize_for_runtime_config: HostfxrInitializeForRuntimeConfig =
        load_symbol(hostfxr_module, b"hostfxr_initialize_for_runtime_config\0")?;
    let get_runtime_delegate: HostfxrGetRuntimeDelegate =
        load_symbol(hostfxr_module, b"hostfxr_get_runtime_delegate\0")?;
    let close: HostfxrClose = load_symbol(hostfxr_module, b"hostfxr_close\0")?;
    if let Ok(set_error_writer) =
        load_symbol::<HostfxrSetErrorWriter>(hostfxr_module, b"hostfxr_set_error_writer\0")
    {
        set_error_writer(hostfxr_error_writer);
        STATE.hostfxr_error_writer_set.store(true, Ordering::SeqCst);
    }

    let runtime_config_w = wide_path(&config.runtime_config_path);
    let game_exe_w = wide_path(&game_exe);
    let dotnet_root_w = wide_path(&dotnet_root);
    let init_params = HostfxrInitializeParameters {
        size: std::mem::size_of::<HostfxrInitializeParameters>(),
        host_path: game_exe_w.as_ptr(),
        dotnet_root: dotnet_root_w.as_ptr(),
    };

    let mut host: HostfxrHandle = null_mut();
    let init_result =
        initialize_for_runtime_config(runtime_config_w.as_ptr(), &init_params, &mut host);
    STATE.hostfxr_initialize_result.store(init_result, Ordering::SeqCst);
    if init_result != 0 || host.is_null() {
        return Err(BootstrapError::HostfxrInitFailed(init_result));
    }

    let mut load_assembly: *mut c_void = null_mut();
    let delegate_result = get_runtime_delegate(
        host,
        HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER,
        &mut load_assembly,
    );
    STATE.hostfxr_delegate_result.store(delegate_result, Ordering::SeqCst);
    close(host);
    if delegate_result != 0 || load_assembly.is_null() {
        return Err(BootstrapError::HostfxrDelegateFailed(delegate_result));
    }

    let load_assembly: LoadAssemblyAndGetFunctionPointer = std::mem::transmute(load_assembly);
    let assembly_w = wide_path(&config.target_assembly);
    let entrypoint_w = wide_null(&entrypoint_type);
    let start_w = wide_null("Start");
    let delegate_w = wide_null("System.Action, System.Private.CoreLib");
    let mut startup: *mut c_void = null_mut();
    let function_result = load_assembly(
        assembly_w.as_ptr(),
        entrypoint_w.as_ptr(),
        start_w.as_ptr(),
        delegate_w.as_ptr(),
        null_mut(),
        &mut startup,
    );
    STATE.load_assembly_result.store(function_result, Ordering::SeqCst);

    if function_result != 0 || startup.is_null() {
        return Err(BootstrapError::LoadAssemblyFailed(function_result));
    }

    log_line("Invoking Doorstop.Entrypoint.Start() through Modstoke Native Host");
    let startup: ManagedAction = std::mem::transmute(startup);
    startup();
    write_state(
        "READY",
        "Modstoke Native Host invoked Doorstop.Entrypoint.Start() successfully.",
        None,
    );
    Ok(())
}

unsafe extern "C" fn hostfxr_error_writer(message: *const u16) {
    if message.is_null() {
        return;
    }

    let mut len = 0usize;
    while *message.add(len) != 0 {
        len += 1;
    }

    let text = String::from_utf16_lossy(std::slice::from_raw_parts(message, len));
    log_line(&format!("hostfxr: {text}"));
}

unsafe fn load_symbol<T>(module: Hmodule, name: &'static [u8]) -> Result<T, BootstrapError> {
    let symbol = GetProcAddress(module, name.as_ptr().cast());
    if symbol.is_null() {
        let display =
            std::str::from_utf8(&name[..name.len().saturating_sub(1)]).unwrap_or("<invalid>");
        return Err(BootstrapError::MissingExport(display.to_string()));
    }

    Ok(std::mem::transmute_copy(&symbol))
}

fn load_config(config_path: &Path, game_dir: &Path) -> Result<NativeHostConfig, BootstrapError> {
    let text = fs::read_to_string(config_path).map_err(|error| {
        BootstrapError::ConfigParseFailed(config_path.to_path_buf(), error.to_string())
    })?;
    let mut section = String::new();
    let mut enabled = true;
    let mut ignore_disable_switch = false;
    let mut redirect_output_log = false;
    let mut target_assembly: Option<PathBuf> = None;
    let mut coreclr_path: Option<PathBuf> = None;
    let mut corlib_dir: Option<PathBuf> = None;
    let mut hostfxr_path: Option<PathBuf> = None;
    let mut runtime_config_path: Option<PathBuf> = None;

    for raw_line in text.lines() {
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with('#') || line.starts_with(';') {
            continue;
        }

        if line.starts_with('[') && line.ends_with(']') {
            section = line[1..line.len() - 1].trim().to_ascii_lowercase();
            continue;
        }

        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        let key = key.trim().to_ascii_lowercase();
        let value = value.trim();

        match (section.as_str(), key.as_str()) {
            ("general", "enabled") => enabled = parse_bool(value, enabled),
            ("general", "ignore_disable_switch") => {
                ignore_disable_switch = parse_bool(value, ignore_disable_switch)
            }
            ("general", "redirect_output_log") => {
                redirect_output_log = parse_bool(value, redirect_output_log)
            }
            ("general", "target_assembly") if !value.is_empty() => {
                target_assembly = Some(resolve_path(game_dir, value))
            }
            ("il2cpp", "coreclr_path") if !value.is_empty() => {
                coreclr_path = Some(resolve_path(game_dir, value))
            }
            ("il2cpp", "corlib_dir") if !value.is_empty() => {
                corlib_dir = Some(resolve_path(game_dir, value))
            }
            ("il2cpp", "hostfxr_path") if !value.is_empty() => {
                hostfxr_path = Some(resolve_path(game_dir, value))
            }
            ("il2cpp", "runtime_config_path") if !value.is_empty() => {
                runtime_config_path = Some(resolve_path(game_dir, value))
            }
            _ => {}
        }
    }

    Ok(NativeHostConfig {
        enabled,
        ignore_disable_switch,
        redirect_output_log,
        target_assembly: target_assembly.unwrap_or_else(|| game_dir.join("Doorstop.dll")),
        coreclr_path,
        corlib_dir,
        hostfxr_path: hostfxr_path.ok_or_else(|| BootstrapError::Generic("Il2Cpp.hostfxr_path is missing.".to_string()))?,
        runtime_config_path: runtime_config_path
            .ok_or_else(|| BootstrapError::Generic("Il2Cpp.runtime_config_path is missing.".to_string()))?,
    })
}

fn validate_bootstrap_config(config: &NativeHostConfig) -> Result<(), BootstrapError> {
    ensure_file("target assembly", &config.target_assembly)?;
    ensure_file("hostfxr", &config.hostfxr_path)?;
    let hostfxr_arch = validate_pe_architecture("hostfxr", &config.hostfxr_path)?;
    STATE.hostfxr_machine.store(hostfxr_arch.machine as usize, Ordering::SeqCst);
    STATE.hostfxr_optional_magic.store(hostfxr_arch.optional_magic as usize, Ordering::SeqCst);
    if let Some(coreclr_path) = &config.coreclr_path {
        ensure_file("coreclr", coreclr_path)?;
        let coreclr_arch = validate_pe_architecture("coreclr", coreclr_path)?;
        STATE.coreclr_machine.store(coreclr_arch.machine as usize, Ordering::SeqCst);
        STATE.coreclr_optional_magic.store(coreclr_arch.optional_magic as usize, Ordering::SeqCst);
    }
    ensure_file("runtimeconfig", &config.runtime_config_path)?;
    if let Some(corlib_dir) = &config.corlib_dir {
        if !corlib_dir.is_dir() {
            log_line(&format!(
                "Configured corlib_dir does not exist; continuing without it: {}",
                corlib_dir.display()
            ));
        }
    }

    Ok(())
}

fn ensure_file(label: &str, path: &Path) -> Result<(), BootstrapError> {
    if path.is_file() {
        Ok(())
    } else {
        Err(BootstrapError::MissingFile(
            label.to_string(),
            path.to_path_buf(),
        ))
    }
}

struct PeArchitecture {
    machine: u16,
    optional_magic: u16,
}

fn validate_pe_architecture(label: &str, path: &Path) -> Result<PeArchitecture, BootstrapError> {
    let bytes = fs::read(path)
        .map_err(|error| BootstrapError::Generic(format!("Failed to read {label} for architecture validation: {error}")))?;
    if bytes.len() < 0x40 || &bytes[0..2] != b"MZ" {
        return Err(BootstrapError::InvalidPeImage(
            label.to_string(),
            path.to_path_buf(),
        ));
    }

    let nt_offset = read_u32(&bytes, 0x3c)? as usize;
    if bytes.len() < nt_offset + 26 {
        return Err(BootstrapError::InvalidPeHeader(
            label.to_string(),
            path.to_path_buf(),
        ));
    }

    let signature = read_u32(&bytes, nt_offset)?;
    if signature != IMAGE_NT_SIGNATURE {
        return Err(BootstrapError::InvalidPeSignature(
            label.to_string(),
            path.to_path_buf(),
        ));
    }

    let machine = read_u16(&bytes, nt_offset + 4)?;
    let optional_magic = read_u16(&bytes, nt_offset + 24)?;
    log_line(&format!(
        "{label} architecture machine=0x{machine:04x}, optional=0x{optional_magic:04x}"
    ));

    if machine != IMAGE_FILE_MACHINE_AMD64 || optional_magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC {
        return Err(BootstrapError::ArchitectureMismatch(
            label.to_string(),
            path.to_path_buf(),
        ));
    }

    Ok(PeArchitecture {
        machine,
        optional_magic,
    })
}

/// Points a module's import of `target_function` at `detour_function`; with no `target_dll`, the import is looked
/// for under every DLL the module imports from, as an API set name may stand in for kernel32.
unsafe fn iat_hook(
    module: Hmodule,
    target_dll: Option<&str>,
    target_function: Farproc,
    detour_function: Farproc,
) -> bool {
    if module.is_null() {
        return false;
    }

    let base = module as usize;
    let mz = base as *const u8;
    if read_process_u16(mz, 0) != 0x5a4d {
        return false;
    }

    let nt_offset = read_process_u32(mz, 0x3c) as usize;
    if read_process_u32(mz, nt_offset) != IMAGE_NT_SIGNATURE {
        return false;
    }

    let optional_offset = nt_offset + 24;
    if read_process_u16(mz, optional_offset) != IMAGE_NT_OPTIONAL_HDR64_MAGIC {
        return false;
    }

    let import_directory_offset = optional_offset + 112 + (IMAGE_DIRECTORY_ENTRY_IMPORT * 8);
    let import_rva = read_process_u32(mz, import_directory_offset) as usize;
    if import_rva == 0 {
        return false;
    }

    let mut descriptor = base + import_rva;
    loop {
        let characteristics = read_process_u32(descriptor as *const u8, 0);
        let name_rva = read_process_u32(descriptor as *const u8, 12) as usize;
        let first_thunk_rva = read_process_u32(descriptor as *const u8, 16) as usize;
        if characteristics == 0 || name_rva == 0 || first_thunk_rva == 0 {
            break;
        }

        let name_ptr = (base + name_rva) as *const c_char;
        let dll_name = CStr::from_ptr(name_ptr).to_string_lossy();
        if target_dll.is_none_or(|wanted| dll_name.eq_ignore_ascii_case(wanted)) {
            let mut thunk = (base + first_thunk_rva) as *mut usize;
            while *thunk != 0 {
                if *thunk == target_function as usize {
                    let mut old_protect: Dword = 0;
                    if VirtualProtect(
                        thunk.cast(),
                        std::mem::size_of::<usize>(),
                        PAGE_READWRITE,
                        &mut old_protect,
                    ) == FALSE
                    {
                        return false;
                    }

                    *thunk = detour_function as usize;
                    let mut restore_protect: Dword = 0;
                    VirtualProtect(
                        thunk.cast(),
                        std::mem::size_of::<usize>(),
                        old_protect,
                        &mut restore_protect,
                    );
                    return true;
                }

                thunk = thunk.add(1);
            }
        }

        descriptor += 20;
    }

    false
}

unsafe fn load_proxy(self_path: &Path, game_dir: &Path) {
    let stem = self_path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("winhttp");
    let alt_path = game_dir.join(format!("{stem}_alt.dll"));
    let real_path = if alt_path.is_file() {
        alt_path
    } else {
        match system_directory() {
            Some(system_dir) => system_dir.join(format!("{stem}.dll")),
            None => PathBuf::from(format!("{stem}.dll")),
        }
    };

    let _ = STATE.proxy_target_path.set(real_path.clone());
    log_line(&format!("Loading proxy target: {}", real_path.display()));
    let real_path_w = wide_path(&real_path);
    let module = LoadLibraryW(real_path_w.as_ptr());
    if module.is_null() {
        log_line("Failed to load proxy target; missing proxy exports will return 0");
        return;
    }

    load_proxy_exports(module);
    log_line("Proxy exports loaded");
}

unsafe fn load_proxy_export(module: Hmodule, name: &'static [u8], slot: *mut usize) -> bool {
    let proc = if module.is_null() {
        null_mut()
    } else {
        GetProcAddress(module, name.as_ptr().cast())
    };
    let target = if proc.is_null() {
        unsupported_proxy_export as *const () as usize
    } else {
        proc as usize
    };

    *slot = target;
    !proc.is_null()
}

unsafe extern "system" fn unsupported_proxy_export() -> usize {
    0
}

macro_rules! proxy_export {
    ($export:ident, $slot:ident) => {
        static mut $slot: usize = 0;

        #[unsafe(no_mangle)]
        #[unsafe(naked)]
        pub unsafe extern "system" fn $export() {
            naked_asm!("jmp qword ptr [rip + {slot}]", slot = sym $slot);
        }
    };
}

include!(concat!(env!("OUT_DIR"), "/proxy_exports.rs"));

fn default_runtime_dir(game_dir: &Path) -> PathBuf {
    let runtime_dir = game_dir.join("Modstoke").join("Runtime");
    let legacy_dir = game_dir.join("OpenACAI").join("Runtime");
    if !runtime_dir.is_dir() && legacy_dir.is_dir() {
        legacy_dir
    } else {
        runtime_dir
    }
}

fn resolve_path(game_dir: &Path, value: &str) -> PathBuf {
    let path = PathBuf::from(value.replace('/', "\\"));
    if path.is_absolute() {
        path
    } else {
        game_dir.join(path)
    }
}

fn parse_bool(value: &str, default: bool) -> bool {
    if value.eq_ignore_ascii_case("true") {
        true
    } else if value.eq_ignore_ascii_case("false") {
        false
    } else {
        default
    }
}

fn derive_dotnet_root(hostfxr_path: &Path) -> Result<PathBuf, BootstrapError> {
    let version_dir = hostfxr_path.parent().ok_or_else(|| {
        format!(
            "hostfxr path has no version dir: {}",
            hostfxr_path.display()
        )
    })?;
    let fxr_dir = version_dir
        .parent()
        .ok_or_else(|| BootstrapError::Generic(format!("hostfxr path has no fxr dir: {}", hostfxr_path.display())))?;
    let host_dir = fxr_dir
        .parent()
        .ok_or_else(|| BootstrapError::Generic(format!("hostfxr path has no host dir: {}", hostfxr_path.display())))?;
    let dotnet_root = host_dir.parent().ok_or_else(|| {
        format!(
            "hostfxr path has no dotnet root: {}",
            hostfxr_path.display()
        )
    })?;

    if dotnet_root.is_dir() {
        Ok(dotnet_root.to_path_buf())
    } else {
        Err(format!(
            "Derived dotnet root does not exist: {}",
            dotnet_root.display()
        ).into())
    }
}

fn system_directory() -> Option<PathBuf> {
    let mut buffer = vec![0u16; MAX_PATH_U32 as usize + 1];
    let len = unsafe { GetSystemDirectoryW(buffer.as_mut_ptr(), MAX_PATH_U32) };
    if len == 0 {
        return None;
    }

    Some(PathBuf::from(String::from_utf16_lossy(
        &buffer[..len as usize],
    )))
}

unsafe fn module_path(module: Hmodule) -> Result<PathBuf, BootstrapError> {
    let mut capacity = MAX_PATH_U32 as usize;
    loop {
        let mut buffer = vec![0u16; capacity + 1];
        let len = GetModuleFileNameW(module, buffer.as_mut_ptr(), capacity as Dword);
        if len == 0 {
            return Err(BootstrapError::Generic("GetModuleFileNameW failed.".to_string()));
        }

        if (len as usize) < capacity {
            return Ok(PathBuf::from(String::from_utf16_lossy(
                &buffer[..len as usize],
            )));
        }

        capacity *= 2;
    }
}

fn wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str()
        .to_string_lossy()
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect()
}

fn set_env(name: &str, value: &str) {
    let name_w = wide_null(name);
    let value_w = wide_null(value);
    unsafe {
        SetEnvironmentVariableW(name_w.as_ptr(), value_w.as_ptr());
    }
}

fn set_env_path(name: &str, value: &Path) {
    set_env(name, &value.display().to_string());
}

fn running_under_wine() -> bool {
    unsafe {
        let ntdll = GetModuleHandleW(wide_null("ntdll.dll").as_ptr());
        !ntdll.is_null() && !GetProcAddress(ntdll, b"wine_get_version\0".as_ptr().cast()).is_null()
    }
}

fn clear_env(name: &str) {
    let name_w = wide_null(name);
    unsafe {
        SetEnvironmentVariableW(name_w.as_ptr(), null());
    }
}

fn log_line(message: &str) {
    let path = STATE.log_path.get().cloned().or_else(|| {
        STATE
            .game_dir
            .get()
            .map(|root| default_runtime_dir(root).join(NATIVE_HOST_LOG_NAME))
    });
    let Some(path) = path else {
        return;
    };

    if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{message}");
    }
}

fn write_state(status: &str, detail: &str, exception_type: Option<&str>) {
    let Some(path) = STATE.state_path.get() else {
        return;
    };

    let config = STATE.config.get();
    let hostfxr_init = STATE.hostfxr_initialize_result.load(Ordering::SeqCst);
    let hostfxr_delegate = STATE.hostfxr_delegate_result.load(Ordering::SeqCst);
    let load_assembly = STATE.load_assembly_result.load(Ordering::SeqCst);
    let state = format!(
        "{{\n  \"schema\": 1,\n  \"serviceKey\": \"openacai.loader.nativeHost\",\n  \"runtime\": \"Modstoke.NativeHost.Rust\",\n  \"status\": \"{}\",\n  \"detail\": \"{}\",\n  \"exceptionType\": \"{}\",\n  \"doorstopTargetOwnedByOpenAcai\": true,\n  \"bootstrapTiming\": \"il2cpp_init\",\n  \"nativeHostPath\": \"{}\",\n  \"proxyTargetPath\": \"{}\",\n  \"proxyExportCount\": {},\n  \"proxyResolvedExportCount\": {},\n  \"proxyMissingExportCount\": {},\n  \"iatHookedUnityPlayer\": {},\n  \"iatHookedExecutable\": {},\n  \"capturedFunctionName\": \"{}\",\n  \"hostfxrArchitectureMachine\": \"{}\",\n  \"hostfxrArchitectureOptionalMagic\": \"{}\",\n  \"coreclrArchitectureMachine\": \"{}\",\n  \"coreclrArchitectureOptionalMagic\": \"{}\",\n  \"hostfxrModuleLoaded\": {},\n  \"hostfxrErrorWriterSet\": {},\n  \"hostfxrInitializeResult\": {},\n  \"hostfxrDelegateResult\": {},\n  \"loadAssemblyResult\": {},\n  \"dotnetRoot\": \"{}\",\n  \"targetDirectory\": \"{}\",\n  \"managedFolderDirectory\": \"{}\",\n  \"entrypointType\": \"{}\",\n  \"appPaths\": \"{}\",\n  \"targetAssembly\": \"{}\",\n  \"hostfxrPath\": \"{}\",\n  \"runtimeConfigPath\": \"{}\"\n}}\n",
        json_escape(status),
        json_escape(detail),
        json_escape(exception_type.unwrap_or("")),
        json_escape(&pathbuf_json_value(STATE.native_host_path.get())),
        json_escape(&pathbuf_json_value(STATE.proxy_target_path.get())),
        EXPECTED_PROXY_EXPORT_COUNT,
        STATE.proxy_resolved_export_count.load(Ordering::SeqCst),
        STATE.proxy_missing_export_count.load(Ordering::SeqCst),
        STATE.iat_hooked_unity_player.load(Ordering::SeqCst),
        STATE.iat_hooked_executable.load(Ordering::SeqCst),
        json_escape(
            STATE.captured_function_name
                .get()
                .map(String::as_str)
                .unwrap_or("")
        ),
        json_escape(&hex_or_empty(STATE.hostfxr_machine.load(Ordering::SeqCst), 4)),
        json_escape(&hex_or_empty(
            STATE.hostfxr_optional_magic.load(Ordering::SeqCst),
            4
        )),
        json_escape(&hex_or_empty(STATE.coreclr_machine.load(Ordering::SeqCst), 4)),
        json_escape(&hex_or_empty(
            STATE.coreclr_optional_magic.load(Ordering::SeqCst),
            4
        )),
        STATE.hostfxr_module_loaded.load(Ordering::SeqCst),
        STATE.hostfxr_error_writer_set.load(Ordering::SeqCst),
        result_code_json(hostfxr_init),
        result_code_json(hostfxr_delegate),
        result_code_json(load_assembly),
        json_escape(&pathbuf_json_value(STATE.dotnet_root_path.get())),
        json_escape(&pathbuf_json_value(STATE.target_directory.get())),
        json_escape(&pathbuf_json_value(STATE.managed_folder_directory.get())),
        json_escape(STATE.entrypoint_type_name.get().map(String::as_str).unwrap_or("")),
        json_escape(STATE.app_paths_value.get().map(String::as_str).unwrap_or("")),
        json_escape(
            &config
                .map(|value| value.target_assembly.display().to_string())
                .unwrap_or_default()
        ),
        json_escape(
            &config
                .map(|value| value.hostfxr_path.display().to_string())
                .unwrap_or_default()
        ),
        json_escape(
            &config
                .map(|value| value.runtime_config_path.display().to_string())
                .unwrap_or_default()
        ),
    );

    let _ = fs::write(path, state);
}

fn pathbuf_json_value(path: Option<&PathBuf>) -> String {
    path.map(|value| value.display().to_string())
        .unwrap_or_default()
}

fn hex_or_empty(value: usize, width: usize) -> String {
    if value == 0 {
        String::new()
    } else {
        format!("0x{value:0width$x}")
    }
}

fn result_code_json(value: i32) -> String {
    if value == UNSET_HOSTFXR_RESULT {
        "null".to_string()
    } else {
        value.to_string()
    }
}

fn json_escape(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut escaped = String::with_capacity(value.len());
    for character in value.chars() {
        match character {
            '\\' => escaped.push_str("\\\\"),
            '"' => escaped.push_str("\\\""),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            '\t' => escaped.push_str("\\t"),
            '\u{0000}'..='\u{001f}' => {
                let byte = character as u8;
                escaped.push_str("\\u00");
                escaped.push(HEX[(byte >> 4) as usize] as char);
                escaped.push(HEX[(byte & 0x0f) as usize] as char);
            }
            _ => escaped.push(character),
        }
    }
    escaped
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, BootstrapError> {
    if bytes.len() < offset + 2 {
        return Err(BootstrapError::Generic("Unexpected end of file while reading u16.".to_string()));
    }

    Ok(u16::from_le_bytes([bytes[offset], bytes[offset + 1]]))
}

fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, BootstrapError> {
    if bytes.len() < offset + 4 {
        return Err(BootstrapError::Generic("Unexpected end of file while reading u32.".to_string()));
    }

    Ok(u32::from_le_bytes([
        bytes[offset],
        bytes[offset + 1],
        bytes[offset + 2],
        bytes[offset + 3],
    ]))
}

unsafe fn read_process_u16(base: *const u8, offset: usize) -> u16 {
    let ptr = base.add(offset).cast::<u16>();
    u16::from_le(ptr.read_unaligned())
}

unsafe fn read_process_u32(base: *const u8, offset: usize) -> u32 {
    let ptr = base.add(offset).cast::<u32>();
    u32::from_le(ptr.read_unaligned())
}

fn is_ordinal(name: *const c_char) -> bool {
    ((name as usize) >> 16) == 0
}
