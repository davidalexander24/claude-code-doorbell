# Doorbell: Windows notifications for Claude Code

Windows toast notifications and louder sounds for [Claude Code](https://code.claude.com), so you notice when Claude needs you or has finished, even with the terminal buried under other windows. Like a doorbell, it rings when someone is waiting for you and stops once you answer. As a bonus, after you answer one of Claude's questions you get a recap of every option it offered, not just the one you picked.

![Three notifications: Claude finished, Claude has a question, Claude needs your approval](docs/screenshot.png)

## What you get

| When | Pop-up | Stays on screen | Sound |
| --- | --- | --- | --- |
| Claude asks you a question | orange **?** "Claude has a question" | until you dismiss it | alert |
| Claude needs approval, or has a plan to review | orange **!** "Claude needs your approval" | until you dismiss it | alert |
| Claude finishes its turn | green **✓** "Claude finished" | about 5 seconds | chime |

- **Shows which session, not what it asked.** The pop-up shows the session's name (your `/rename` name, otherwise Claude Code's own title for it) and the folder or session handle. The question and the command never appear, so nothing sensitive sits on your screen.
- **Never piles up.** Each session keeps at most one pop-up of each kind: a new one replaces the old one, answering a question clears its pop-up, and a finished turn clears a pending question.
- **Louder sounds.** The standard Windows sounds, boosted by 14 dB by default. The boosted copies are made on your machine the first time they play, so no sound files ship with the plugin.
- **Question recap.** Claude Code normally keeps only "question → answer" once you reply. This adds every question with all of its options, your picks marked:

  ```
  ⎿  PostToolUse:AskUserQuestion says:

     [Payments] Which payment provider?
        ● Stripe - Cards and wallets
        ○ PayPal - Checkout redirect
        → Notes: fees matter

     [Regions] Which regions at launch? (multi-select)
        ● EU - VAT handling needed
        ○ US - Sales tax by state
        ● Indonesia - Rupiah, QRIS
  ```

  The recap is shown to you only; it is not sent to Claude.

## Background

This started as two sound hooks in my own Claude Code settings, so I'd hear when Claude finished or needed my approval while I was in another window. Sounds alone weren't enough to tell which session wanted me, or whether it was a question or just a finished turn, so they grew into these pop-ups, the louder sounds and the question recap, packaged here so anyone on Windows can install them in one line.

## Requirements

- Windows 10 or 11, with the built-in Windows PowerShell 5.1. Nothing else to install.
- A recent Claude Code with plugin support. Tested on 2.1.330.

## Install

At the Claude Code prompt:

```
/plugin install doorbell --marketplace davidalexander24/claude-code-doorbell
```

Answer `y` to add the marketplace, then pick a scope. From a shell instead:

```
claude plugin install doorbell --marketplace davidalexander24/claude-code-doorbell
```

If your Claude Code doesn't know `--marketplace` yet:

```
/plugin marketplace add davidalexander24/claude-code-doorbell
/plugin install doorbell@claude-code-doorbell
```

Then restart Claude Code (or run `/reload-plugins`) so the hooks load.

## Settings

Everything works out of the box. To change something, set environment variables in the `env` block of `~/.claude/settings.json`:

```json
{
  "env": {
    "CC_NOTIFY_GAIN_DB": "10",
    "CC_NOTIFY_DONE": "0"
  }
}
```

| Variable | Default | What it does |
| --- | --- | --- |
| `CC_NOTIFY_GAIN_DB` | `14` | Volume boost for the sounds, in decibels. `0` plays them unchanged. |
| `CC_NOTIFY_SOUND` | `1` | `0` turns all sounds off. |
| `CC_NOTIFY_DONE` | `1` | `0` turns off the "Claude finished" pop-up and chime. |
| `CC_NOTIFY_PERSIST` | `1` | `0` lets question and approval pop-ups time out (about 25 seconds) instead of waiting for you. |
| `CC_NOTIFY_RECAP` | `1` | `0` turns off the question recap. |
| `CC_NOTIFY_ATTENTION_SOUND` | `Windows Error.wav` | Path to your own `.wav` for questions and approvals. |
| `CC_NOTIFY_DONE_SOUND` | `Windows Notify.wav` | Path to your own `.wav` for finished turns. |
| `CC_NOTIFY_APP_NAME` | `Claude Code` | The name shown in the pop-up's header. |

Custom sounds are boosted too when they are 16-bit PCM WAV files; any other format plays as it is.

## How it works

The plugin adds three hooks, all running [`scripts/notify.ps1`](scripts/notify.ps1):

| Hook | Fires when | Does |
| --- | --- | --- |
| `PermissionRequest` | Claude is about to ask for approval, asks a question (`AskUserQuestion`) or presents a plan (`ExitPlanMode`) | question or approval pop-up, alert sound |
| `Stop` | Claude finishes its turn | "finished" pop-up, chime |
| `PostToolUse` on `AskUserQuestion` | you answered a question | clears the question pop-up, prints the recap |

Pop-ups use the Windows notification API through PowerShell's WinRT support, under an app identity registered at `HKCU\Software\Classes\AppUserModelId\ClaudeCodeDoorbell`. Boosted sounds and a copy of the icons are kept in `%LOCALAPPDATA%\claude-code-doorbell`, along with `notify.log` if anything goes wrong.

The session name comes from the session's transcript and from `~/.claude/sessions`. These are Claude Code's internal files and may change between versions; if the name can't be found, the pop-up shows the folder name instead.

## Limitations

- **Windows only.**
- **Do Not Disturb hides the banners.** With Do Not Disturb or Focus on, or Windows' automatic rules (for example while a game or full-screen app runs), notifications go straight to the notification center without popping up.
- **The recap has a label you can't remove.** Claude Code puts `PostToolUse:AskUserQuestion says:` in front of anything a hook prints, and hooks can't hide it ([#99048](https://github.com/anthropics/claude-code/issues/99048)). Showing all options natively is requested in [#111703](https://github.com/anthropics/claude-code/issues/111703).
- **Approval pop-ups wait for you.** They stay until you dismiss them or the session finishes its turn. Clearing them the moment you approve would mean running a script after every tool call.
- **No notifications in `claude -p`.** Background hooks are cancelled when a print-mode session exits.
- **Duplicate browser notifications.** If you use Remote Control and have claude.ai open in a browser, claude.ai may send its own notifications too. Block notifications for claude.ai in that browser if you only want these.

## Troubleshooting

No pop-up at all:

1. Check **Settings → System → Notifications**: notifications on, **Claude Code** allowed, Do Not Disturb off.
2. Look in `%LOCALAPPDATA%\claude-code-doorbell\notify.log`.
3. From a clone of this repo, fire one by hand:

   ```powershell
   '{"tool_name":"AskUserQuestion","cwd":"C:\\dev\\demo"}' | powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\notify.ps1 -Kind attention
   ```

## Uninstall

```
claude plugin uninstall doorbell
```

To also remove the cached sounds and icons and the app identity:

```powershell
Remove-Item -Recurse "$env:LOCALAPPDATA\claude-code-doorbell"
Remove-Item "HKCU:\Software\Classes\AppUserModelId\ClaudeCodeDoorbell"
```

## License

[MIT](LICENSE). An unofficial community plugin, not affiliated with or endorsed by Anthropic. Claude and Claude Code are trademarks of Anthropic.
