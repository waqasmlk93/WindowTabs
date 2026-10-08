namespace Bemo
open System
open System.Text.RegularExpressions

module ExclusionRules =
    //'*' and '?' wildcards, case-insensitive. A pattern containing a path separator is matched against the full
    //process path, anything else against the exe file name only (so 'chrome.exe' or 'ms-*.exe' just work)
    let matches (pattern:string) (processPath:string) =
        let pattern = pattern.Trim()
        if pattern.Length = 0 || String.IsNullOrEmpty(processPath) then false
        else
            let text = if pattern.Contains("\\") || pattern.Contains("/") then processPath else System.IO.Path.GetFileName(processPath)
            let regex = "^" + Regex.Escape(pattern.Replace('/', '\\')).Replace("\\*", ".*").Replace("\\?", ".") + "$"
            Regex.IsMatch(text.Replace('/', '\\'), regex, RegexOptions.IgnoreCase)

type FilterService() as this =
    let os = OS()
    let blackListedExeNames = 
        let names = Set2(List2(["taskmgr.exe"]))
        if System.Diagnostics.Debugger.IsAttached then names.add("devenv.exe") else names

    member this.includedPaths 
        with get() = Services.settings.getValue("includedPaths").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("includedPaths", box(value))

     member this.excludedPaths 
        with get() = Services.settings.getValue("excludedPaths").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("excludedPaths", box(value))
           
    member this.isTabbingEnabledForAllProcessesByDefault 
        with get() = Services.settings.getValue("enableTabbingByDefault").cast<bool>()
        and set(value) = 
            Services.settings.setValue("enableTabbingByDefault", box(value))
            Services.program.refresh().ignore

    member this.isBanned (window:Window) =
        blackListedExeNames.contains(window.pid.exeName)

    member this.isAppWindowStyle (window:Window) = 
        let style = IntPtr( 
            WindowsStyles.WS_OVERLAPPEDWINDOW &&&
            ~~~WindowsStyles.WS_CAPTION &&&
            ~~~WindowsStyles.WS_THICKFRAME &&&
            ~~~WindowsStyles.WS_SYSMENU
        )
        Win32Helper.IntPtrAnd(window.styleEx, IntPtr(WindowsExtendedStyles.WS_EX_TOOLWINDOW)) = IntPtr.Zero &&
        Win32Helper.IntPtrAnd(window.style, style) = style
    
    member this.isAppWindow (window:Window) =
        let tests = List2([
            fun() -> window.pid.canQueryProcess
            fun() -> this.isAppWindowStyle(window)
            fun() -> window.pid.isCurrentProcess.not
            fun() -> window.isWindow
            fun() -> window.isVisible
            fun() -> window.isTopMost.not
            fun() -> this.isValidOwner(window)
            //Win32 Dialogue class
            fun() -> window.className <> "#32770"
            fun() -> (window.className <> "ApplicationFrameWindow") || not(String.IsNullOrEmpty window.text)
            fun() -> this.isBanned(window).not
            fun() -> this.isOnScreenOrMinimized(window)
            ])
        tests.all(fun pred -> pred()) 

    member this.isValidOwner (window:Window) =
        let owner = window.parent
        if owner.hwnd = IntPtr.Zero then true
        // Delphi and MFC have owner windows w/ zero size.
        else owner.bounds.width = 0

    member this.screenRegion = os.screenRegion

    member this.isOnScreenOrMinimized(window:Window) =
        window.isMinimized || this.screenRegion.containsRect(window.bounds)
    
    member this.getIsTabbingEnabledForProcess(processPath) =
        if this.isTabbingEnabledForAllProcessesByDefault then
            this.excludedPaths.contains(processPath).not
        else
            this.includedPaths.contains(processPath)

    member this.excludeChildProcessesPaths
        with get() = Services.settings.getValue("excludeChildProcessesPaths").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("excludeChildProcessesPaths", box(value))

    member this.excludedPatterns
        with get() = Services.settings.getValue("excludedPatterns").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("excludedPatterns", box(value))

    member private this.isSamePath (a:string) (b:string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    //windows an app's own rules keep out of groups: the exclusion patterns, and windows of processes that were
    //launched (directly or not) by an app that has 'exclude sub-processes' switched on. The launching app's own
    //windows are not affected.
    member this.isExcludedByRules(window:Window) =
        let processPath = window.pid.processPath
        let patterns = this.excludedPatterns
        let byPattern = patterns.count > 0 && patterns.items.any(fun pattern -> ExclusionRules.matches pattern processPath)
        let parents = this.excludeChildProcessesPaths
        byPattern ||
            (parents.count > 0 &&
                window.pid.ancestorPaths |> List.exists(fun ancestor -> parents.items.any(fun parent -> this.isSamePath parent ancestor)))

    member this.isTabbableWindow(window:Window) = 
        this.getIsTabbingEnabledForProcess(window.pid.processPath) &&
        this.isAppWindow(window) &&
        this.isExcludedByRules(window).not

    interface IFilterService with
        
        member x.isAppWindow(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isAppWindow(window)

        member x.isAppWindowStyle(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isAppWindowStyle(window)

        member x.isTabbableWindow(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isTabbableWindow(window)

        member x.isTabbingEnabledForAllProcessesByDefault
            with get() = this.isTabbingEnabledForAllProcessesByDefault
            and set(value) = this.isTabbingEnabledForAllProcessesByDefault <- value

        member x.setIsTabbingEnabledForProcess processPath enabled = 
            if this.isTabbingEnabledForAllProcessesByDefault then
                this.excludedPaths <-
                    if enabled then
                        this.excludedPaths.remove processPath
                    else
                        this.excludedPaths.add processPath
            else
                this.includedPaths <-
                    if enabled then
                        this.includedPaths.add processPath
                    else
                        this.includedPaths.remove processPath
                
            Services.program.refresh().ignore

        member x.getIsTabbingEnabledForProcess(processPath) = 
            this.getIsTabbingEnabledForProcess(processPath)

        member x.getExcludeChildProcesses(processPath) =
            this.excludeChildProcessesPaths.items.any(fun p -> this.isSamePath p processPath)

        member x.setExcludeChildProcesses processPath excluded =
            this.excludeChildProcessesPaths <-
                if excluded then this.excludeChildProcessesPaths.add processPath
                else this.excludeChildProcessesPaths.remove processPath
            Services.program.refresh().ignore

        member x.excludedPatterns
            with get() = this.excludedPatterns
            and set(value) =
                this.excludedPatterns <- value
                Services.program.refresh().ignore

