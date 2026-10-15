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

struct HostState {
    game_dir: OnceLock<PathBuf>,
    log_path: OnceLock<PathBuf>,
    state_path: OnceLock<PathBuf>,
    config: OnceLock<NativeHostConfig>,
    bootstrap_started: AtomicBool,
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
    MissingParentDir(PathBuf),
    LoadLibraryFailed(String),
    MissingExport(String),
    ConfigParseFailed(PathBuf, String),
    MissingConfigValue(String),
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
            Self::MissingParentDir(p) => write!(f, "Path has no parent: {}", p.display()),
            Self::LoadLibraryFailed(s) => write!(f, "Failed to load library: {s}"),
            Self::MissingExport(s) => write!(f, "Missing native export: {s}"),
            Self::ConfigParseFailed(p, e) => write!(f, "Failed to read {}: {e}", p.display()),
            Self::MissingConfigValue(s) => write!(f, "Config missing required value: {s}"),
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
    fn GetModuleFileNameW(module: Hmodule, filename: *mut u16, size: Dword) -> Dword;
    fn GetModuleHandleW(module_name: *const u16) -> Hmodule;
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
    let runtime_dir = game_dir.join("OpenACAI").join("Runtime");
    let _ = fs::create_dir_all(&runtime_dir);
    let _ = STATE.log_path.set(runtime_dir.join("openacai-native-host.log"));
    let _ = STATE.state_path.set(runtime_dir.join("openacai-native-host-state.json"));

    log_line("OpenACAI Rust native host attached");
    log_line(&format!("Executable path: {}", game_exe.display()));
    log_line(&format!("Game directory: {}", game_dir.display()));

    let cwd_w = wide_path(&game_dir);
    SetCurrentDirectoryW(cwd_w.as_ptr());

    let self_path = module_path(hinst_dll)?;
    let _ = STATE.native_host_path.set(self_path.clone());
    log_line(&format!("Native host path: {}", self_path.display()));
    load_proxy_exports(null_mut());
    load_proxy(&self_path, &game_dir);

    let config_path = game_dir.join("doorstop_config.ini");
    let config = load_config(&config_path, &game_dir)?;
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

    install_get_proc_address_hook()?;
    set_env("DOORSTOP_DISABLE", "TRUE");
    write_state(
        "HOOKED",
        "OpenACAI Rust native host installed the IL2CPP bootstrap hook.",
        None,
    );
    Ok(())
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
        let unity_hooked = iat_hook(unity, "kernel32.dll", target, hook);
        STATE.iat_hooked_unity_player.store(unity_hooked, Ordering::SeqCst);
        hooked |= unity_hooked;
    }

    if !exe.is_null() && exe != unity {
        let exe_hooked = iat_hook(exe, "kernel32.dll", target, hook);
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
    }

    GetProcAddress(module, name)
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
        if let Err(detail) = hostfxr_bootstrap() {
            log_line(&format!("hostfxr bootstrap failed: {detail}"));
            write_state("FAILED", &detail.to_string(), Some("HostfxrBootstrapFailure"));
        }
    }

    result
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

    log_line("Invoking Doorstop.Entrypoint.Start() through OpenACAI Rust native host");
    let startup: ManagedAction = std::mem::transmute(startup);
    startup();
    write_state(
        "READY",
        "OpenACAI Rust native host invoked Doorstop.Entrypoint.Start() successfully.",
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
        return Err(format!("Missing native export: {display}").into());
    }

    Ok(std::mem::transmute_copy(&symbol))
}

fn load_config(config_path: &Path, game_dir: &Path) -> Result<NativeHostConfig, BootstrapError> {
    let text = fs::read_to_string(config_path)
        .map_err(|error| BootstrapError::Generic(format!("Failed to read {}: {error}", config_path.display())))?;
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
        Err(format!("{label} does not exist: {}", path.display()).into())
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
        return Err(format!(
            "{label} is not a valid PE image: {}",
            path.display()
        ).into());
    }

    let nt_offset = read_u32(&bytes, 0x3c)? as usize;
    if bytes.len() < nt_offset + 26 {
        return Err(format!(
            "{label} has an invalid PE header offset: {}",
            path.display()
        ).into());
    }

    let signature = read_u32(&bytes, nt_offset)?;
    if signature != IMAGE_NT_SIGNATURE {
        return Err(format!(
            "{label} has an invalid NT signature: {}",
            path.display()
        ).into());
    }

    let machine = read_u16(&bytes, nt_offset + 4)?;
    let optional_magic = read_u16(&bytes, nt_offset + 24)?;
    log_line(&format!(
        "{label} architecture machine=0x{machine:04x}, optional=0x{optional_magic:04x}"
    ));

    if machine != IMAGE_FILE_MACHINE_AMD64 || optional_magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC {
        return Err(format!(
            "{label} architecture does not match the x64 game process: {}",
            path.display()
        ).into());
    }

    Ok(PeArchitecture {
        machine,
        optional_magic,
    })
}

unsafe fn iat_hook(
    module: Hmodule,
    target_dll: &str,
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
        if dll_name.eq_ignore_ascii_case(target_dll) {
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

fn clear_env(name: &str) {
    let name_w = wide_null(name);
    unsafe {
        SetEnvironmentVariableW(name_w.as_ptr(), null());
    }
}

fn log_line(message: &str) {
    let path = STATE.log_path.get().cloned().or_else(|| {
        STATE.game_dir.get().map(|root| {
            root.join("OpenACAI")
                .join("Runtime")
                .join("openacai-native-host.log")
        })
    });
    let Some(path) = path else {
        return;
    };

    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

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
        "{{\n  \"schema\": 1,\n  \"serviceKey\": \"openacai.loader.nativeHost\",\n  \"runtime\": \"OpenACAI.Endnight.Loader.NativeHost.Rust\",\n  \"status\": \"{}\",\n  \"detail\": \"{}\",\n  \"exceptionType\": \"{}\",\n  \"doorstopTargetOwnedByOpenAcai\": true,\n  \"bootstrapTiming\": \"il2cpp_init\",\n  \"nativeHostPath\": \"{}\",\n  \"proxyTargetPath\": \"{}\",\n  \"proxyExportCount\": {},\n  \"proxyResolvedExportCount\": {},\n  \"proxyMissingExportCount\": {},\n  \"iatHookedUnityPlayer\": {},\n  \"iatHookedExecutable\": {},\n  \"capturedFunctionName\": \"{}\",\n  \"hostfxrArchitectureMachine\": \"{}\",\n  \"hostfxrArchitectureOptionalMagic\": \"{}\",\n  \"coreclrArchitectureMachine\": \"{}\",\n  \"coreclrArchitectureOptionalMagic\": \"{}\",\n  \"hostfxrModuleLoaded\": {},\n  \"hostfxrErrorWriterSet\": {},\n  \"hostfxrInitializeResult\": {},\n  \"hostfxrDelegateResult\": {},\n  \"loadAssemblyResult\": {},\n  \"dotnetRoot\": \"{}\",\n  \"targetDirectory\": \"{}\",\n  \"managedFolderDirectory\": \"{}\",\n  \"entrypointType\": \"{}\",\n  \"appPaths\": \"{}\",\n  \"targetAssembly\": \"{}\",\n  \"hostfxrPath\": \"{}\",\n  \"runtimeConfigPath\": \"{}\"\n}}\n",
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

    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

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
    value
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
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
