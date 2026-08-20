use aviutl2::{
    AnyResult,
    generic::{GenericPlugin, GenericPluginTable, GlobalEditHandle, HostAppHandle},
};
use std::{
    fs::{self, OpenOptions},
    io::Write,
    panic::{AssertUnwindSafe, catch_unwind},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicBool, Ordering},
    thread,
};
use windows_sys::Win32::System::LibraryLoader::{
    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
    GetModuleFileNameW, GetModuleHandleExW,
};

const TITLE: &str = "AviUtl2 FAT v1.0";
static STARTING: AtomicBool = AtomicBool::new(false);
static EDIT_HANDLE: GlobalEditHandle = GlobalEditHandle::new();

#[aviutl2::plugin(GenericPlugin)]
struct FatPlugin;

impl GenericPlugin for FatPlugin {
    fn new(_: aviutl2::AviUtl2Info) -> AnyResult<Self> {
        log("FAT plugin loaded");
        Ok(Self)
    }
    fn plugin_info(&self) -> GenericPluginTable {
        GenericPluginTable {
            name: TITLE.into(),
            information: "Formation Auto Text / isolated FAT Worker integration".into(),
        }
    }
    fn register(&mut self, host: &mut HostAppHandle) {
        EDIT_HANDLE.init(host.create_edit_handle());
        // The SDK uses '\\' to create menu levels.  Keep FAT with the other
        // user-facing plugins: 編集 → プラグイン → AviUtl2 FAT → FAT を開く.
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\FAT を開く", open_fat);
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\動画を選択して開始", open_media);
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\ログを開く", open_logs);
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\AviUtl2 FAT について", open_about);
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\開発者\\Effect一覧をログ出力", dump_effects);
        host.register_edit_menu("プラグイン\\AviUtl2 FAT\\開発者\\Effect設定項目をログ出力", dump_effect_items);
    }
}

fn open_fat() {
    schedule("main");
}
fn open_media() {
    schedule("recognition");
}
fn open_logs() {
    schedule("logs");
}
fn open_about() {
    schedule("about");
}
fn dump_effects() {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !EDIT_HANDLE.is_ready() { log("EFFECT_ENUMERATION_UNAVAILABLE: edit handle is not ready"); return; }
        let effects = EDIT_HANDLE.get_effects();
        log(&format!("EFFECT_ENUMERATION_BEGIN count={}", effects.len()));
        for (index, effect) in effects.iter().enumerate() {
            log(&format!("Effect[{index}]: name={:?} type={:?}", effect.name, effect.effect_type));
        }
        log("EFFECT_ENUMERATION_END");
    }));
}
fn dump_effect_items() {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !EDIT_HANDLE.is_ready() { log("EFFECT_ITEM_ENUMERATION_UNAVAILABLE: edit handle is not ready"); return; }
        let effects = EDIT_HANDLE.get_effects();
        log(&format!("EFFECT_ITEM_ENUMERATION_BEGIN effects={}", effects.len()));
        for effect in effects {
            log(&format!("EffectPropertiesBegin: name={:?} type={:?}", effect.name, effect.effect_type));
            match EDIT_HANDLE.get_effect_items(&effect.name) {
                Ok(items) => for item in items { log(&format!("Property: name={:?} type={:?}", item.name, item.item_type)); },
                Err(error) => log(&format!("PropertyEnumerationError: effect={:?} error={error:?}", effect.name)),
            }
            log("EffectPropertiesEnd");
        }
        log("EFFECT_ITEM_ENUMERATION_END");
    }));
}
fn schedule(target: &'static str) {
    if STARTING.swap(true, Ordering::SeqCst) {
        return;
    }
    thread::spawn(move || {
        let _ = catch_unwind(AssertUnwindSafe(|| launch(target)));
        STARTING.store(false, Ordering::SeqCst);
    });
}
fn launch(target: &str) {
    let Some(exe) = find_app() else {
        log("FAT_APP_NOT_FOUND: expected FAT\\AviUtl2FAT.App.exe beside the loaded .aux2");
        return;
    };
    match Command::new(&exe)
        .arg("--aviutl2")
        .arg("--host-pid")
        .arg(std::process::id().to_string())
        .arg("--target")
        .arg(target)
        .spawn()
    {
        Ok(_) => log(&format!("FAT_APP_STARTED: {}", exe.display())),
        Err(error) => log(&format!("FAT_APP_START_FAILED: {} ({error})", exe.display())),
    }
}
fn find_app() -> Option<PathBuf> {
    let plugin = module_path()?;
    let base = plugin.parent().unwrap_or(Path::new("."));
    [
        base.join("AviUtl2FAT.App.exe"),
        base.join("FAT").join("AviUtl2FAT.App.exe"),
    ]
    .into_iter()
    .find(|path| path.is_file())
}
fn module_path() -> Option<PathBuf> {
    let mut module = std::ptr::null_mut();
    let address = module_path as *const () as *const u16;
    if unsafe {
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            address,
            &mut module,
        )
    } == 0
    {
        return None;
    }
    let mut buffer = vec![0u16; 32768];
    let length =
        unsafe { GetModuleFileNameW(module, buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
    (length > 0).then(|| PathBuf::from(String::from_utf16_lossy(&buffer[..length])))
}
fn log(message: &str) {
    if let Some(root) = std::env::var_os("LOCALAPPDATA") {
        let dir = PathBuf::from(root).join("AviUtl2FAT").join("logs");
        if fs::create_dir_all(&dir).is_ok() {
            if let Ok(mut file) = OpenOptions::new()
                .create(true)
                .append(true)
                .open(dir.join("plugin_bootstrap.log"))
            {
                let _ = writeln!(file, "{message}");
            }
        }
    }
}
aviutl2::register_generic_plugin!(FatPlugin);
