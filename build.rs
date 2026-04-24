// Embeds a Windows UAC manifest. Release builds request HighestAvailable so the
// switcher can auto-elevate to match Task Manager's integrity level (UIPI blocks
// our WH_KEYBOARD_LL hook from receiving events destined for a higher-integrity
// foreground window otherwise). Debug builds stay AsInvoker so `cargo run` works
// without a UAC prompt on every rebuild.

use embed_manifest::{embed_manifest, new_manifest};
use embed_manifest::manifest::ExecutionLevel;

fn main() {
    if std::env::var_os("CARGO_CFG_WINDOWS").is_some() {
        let level = match std::env::var("PROFILE").as_deref() {
            Ok("release") => ExecutionLevel::HighestAvailable,
            _ => ExecutionLevel::AsInvoker,
        };
        embed_manifest(
            new_manifest("WheelSwitcher").requested_execution_level(level),
        )
        .expect("unable to embed manifest");
    }
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=PROFILE");
}
