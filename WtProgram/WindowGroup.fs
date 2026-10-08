namespace Bemo
open System
open System.Diagnostics
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Reflection
open System.Threading
open System.Windows.Forms
open Bemo.Win32.Forms

type SplitInfo = {
    left: IntPtr
    right: IntPtr
    state: SplitState
    }

type WindowGroup(enableSuperBar:bool, plugins:List2<IPlugin>) as this =
    let Cell = CellScope(true)
    let _bb = Blackboard()
    let invoker = InvokerService.invoker
    let _os = OS()
    let addedEvent = Event<_>()
    let movedEvent = Event<IntPtr*int>()
    let removedEvent = Event<_>()
    let exitedEvent = Event<_>()
    let mouseLLEvent = Event<Int32 * Pt * IntPtr>()
    let flashEvent = Event<_>()
    let keyboardLLEvent = Event<Int32 * KBDLLHOOKSTRUCT>()
    let foregroundEvent = Event<_>()
    let appearanceChangedEvent = Event<_>()

    let isDestroyed = Cell.create(false)
    let zorderCell = Cell.create(List2<IntPtr>())
    let prevTop = Cell.create(None)
    let placement = Cell.create(None:Option<Rect * OSWindowPlacement>)
    let windowsCell = Cell.create(Set2())
    let _ts = ref None 
    let inMoveSize = Cell.create(false)
    let foregroundCell = Cell.create(_os.foreground.hwnd)
    let prevForegroundCell = ref None
    let isMinimized hwnd = this.os.windowFromHwnd(hwnd).isMinimized
    let hookCleanup = Cell.create(Map2<IntPtr, IDisposable>())
    let shellHookWindow = Cell.create(None)
    let winEventHandler = Cell.create(None)
    let isDraggingCell = Cell.create(false)
    let isDraggingExport = Cell.export <| fun() -> isDraggingCell.value
    let zorderExport = Cell.export <| fun() -> zorderCell.value
    let isVisibleCell = Cell.create(false)
    //while the top window is being dragged, resized, maximized or restored the other tabs are parked off screen and
    //only placed behind it again once it has been still for a moment; they never chase it, so they are never seen
    let isParked = ref false
    //split view: two tabs side by side in one frame (see SplitLayout.fs)
    let splitRef : SplitInfo option ref = ref None
    //where each tile really is after we placed it; a tile whose edges still match is not the user changing anything
    let expectedTiles : Map<IntPtr, SplitBox> ref = ref Map.empty
    //the window the user is moving or sizing right now
    let driverRef = ref IntPtr.Zero
    //which tab was on top (and whether the group had the focus) when the others were parked
    let parkedTop = ref IntPtr.Zero
    let parkedWasForeground = ref false
    let lastMotionTick = ref 0
    let settleTicker = new System.Windows.Forms.Timer()
    //follow-up checks of the tab bar's place in the z-order after tabs were placed (the apps do it a little later)
    let stripChecks : IDisposable list ref = ref []
    //true while some window of the group has been made smaller to leave the docked bar its row
    let dockActive = ref false

    let isMaximizedExport = Cell.export <| fun() ->
        zorderCell.value.tryHead.exists(fun hwnd -> this.os.windowFromHwnd(hwnd).isMaximized)
 
    let boundsExport = Cell.export <| fun() ->
        placement.value.bind <| fun(rect,placement) -> 
            if isVisibleCell.value then Some(rect) else None

    let isForegroundExport = Cell.export <| fun() ->
        zorderCell.value.any((=) foregroundCell.value)

    member this.isSuperBarEnabled = enableSuperBar

    member this.init(ts:TabStrip) =
        _ts := Some(ts)

        //one timer that is started by motion and checks how long the top window has been still
        settleTicker.Interval <- 40
        settleTicker.Tick.Add <| fun _ ->
            if Environment.TickCount - lastMotionTick.Value >= 120 then
                settleTicker.Stop()
                this.invokeAsync <| fun() -> this.settleNow()

        winEventHandler.set(Some(
            _os.setSingleWinEvent WinEvent.EVENT_SYSTEM_FOREGROUND <| fun(hwnd) -> 
                this.main(hwnd, WinEvent.EVENT_SYSTEM_FOREGROUND)))
            
        shellHookWindow.set(Some(_os.registerShellHooks this.shellEvents))
        
            
        isMaximizedExport.init()
        isDraggingExport.init()
        zorderExport.init()
        boundsExport.init()
        isForegroundExport.init()

        //the look is the program's own look when it has one, so it follows the active tab of the group
        Cell.listen <| fun() ->
            this.refreshAppearance()
        let onAppearanceSetting = fun(_) ->
            this.invokeAsync <| fun() ->
                this.refreshAppearance()
        Services.settings.notifyValue "tabAppearance" onAppearanceSetting
        Services.settings.notifyValue "appAppearance" onAppearanceSetting

        Cell.listen <| fun() ->
            this.ts.zorder <- zorderCell.value.map(Tab)
            
        Cell.listen <| fun() ->
            this.ts.foreground <- this.foregroundTab
        
        Cell.listen <| fun() ->
            //this is important, we dont' want to leave the parent set to the previous hwnd
            //which was removed, this can cause issues when that window gets added to another
            //group on another thread during drag / drop
            this.setTsParent(if this.isEmpty.not then zorderCell.value.head else IntPtr.Zero)

        Cell.listen <| fun() ->
            this.ts.visible <- isVisibleCell.value

        Services.registerLocal(this)

        plugins.iter <| fun p -> p.init()

    member this.foreground
        with get() = foregroundCell.value
        and set(value) =
            let prev = foregroundCell.value
            if prev <> value then
                foregroundCell.set(value)
                foregroundEvent.Trigger()

    member this.foregroundTab =
        if this.windows.contains(this.foreground) then
            Some(Tab(this.foreground))
        else
            None

    member this.postMouseLL(msg, pt, data) = mouseLLEvent.Trigger(msg, pt, data)
    member this.postKeyboardLL(key, data) = keyboardLLEvent.Trigger(key, data)
    member this.mouseLL = mouseLLEvent.Publish
    member this.keyboardLL = keyboardLLEvent.Publish
    member this.bb = _bb
    member this.ts : TabStrip = _ts.Value.Value
    
    member this.isPointInTs (pt:Pt) =
        let hwnd = Win32Helper.GetTopLevelWindowFromPoint(pt.Point)
        this.ts.hwnd = hwnd

    member this.isPointInGroup (pt:Pt) =
        let hwnd = Win32Helper.GetTopLevelWindowFromPoint(pt.Point)
        this.ts.hwnd = hwnd || this.windows.contains(hwnd)
    
    member this.topWindow = zorderCell.value.head
   

    member this.windows : Set2<IntPtr> = windowsCell.value

    
    member this.tabAppearance =
        let globalAppearance = Services.settings.getValue("tabAppearance").cast<TabAppearanceInfo>()
        if zorderCell.value.isEmpty then globalAppearance
        else
            let processPath = this.os.windowFromHwnd(zorderCell.value.head).pid.processPath
            let overrides = Services.settings.getValue("appAppearance").cast<Map2<string, TabAppearanceInfo>>()
            overrides.tryFind(processPath).def(globalAppearance)

    member private this.refreshAppearance() =
        this.ts.setTabAppearance(this.tabAppearance)
        appearanceChangedEvent.Trigger()

    member this.appearanceChanged = appearanceChangedEvent.Publish

    member private this.withUpdate f =
        Cell.beginUpdate()
        let result = f()
        Cell.endUpdate()
        result

    member this.invokeSync f =
        invoker.invoke (fun() -> this.withUpdate f)

    member this.invokeAsync f =
        invoker.asyncInvoke <| fun() -> this.withUpdate f

    member private this.updateIsVisible() =
        isVisibleCell.value <-
            this.isEmpty.not &&
            zorderCell.value.where(isMinimized >> not).tryHead.IsSome &&
            inMoveSize.value.not
            
    member private this.updatePlacements() =
        if splitRef.Value.IsSome then
            //in a split group the two tiles are laid out by layoutSplit, every other tab (even one that was just
            //swapped out and is still first in the z-order) waits behind them at the size of the frame
            zorderCell.value.where(fun (hwnd:IntPtr) -> this.isTile(hwnd).not).iter(this.adjustWindowPlacement)
        else
            //every tab that is placed is put straight back behind the one before it: resizing or maximizing a window
            //can raise it, and a raised tab is a flash of the wrong window
            let above = ref zorderCell.value.head
            zorderCell.value.tail.iter <| fun hwnd ->
                this.adjustWindowPlacement(hwnd)
                this.os.windowFromHwnd(hwnd).insertAfterAsync(above.Value)
                above := hwnd

    member private this.adjustChildWindows = fun() ->
        this.updatePlacements()
        
    member private this.focusDriverOrTop(hwnd) =
        if this.isTile(hwnd) then this.os.windowFromHwnd(hwnd).setForeground(false)
        else this.makeTopWindowForeground()

    member private this.makeTopWindowForeground() =
        match zorderCell.value.where(isMinimized >> not).tryHead with
        | Some(top) -> 
            let window = this.os.windowFromHwnd(top)
            window.setForeground(false)
        | None -> ()

    member private this.parkChildren() =
        if isParked.Value.not then
            isParked := true
            parkedTop := zorderCell.value.head
            parkedWasForeground := this.windows.contains(this.os.foreground.hwnd)
            this.hideChildWindows()

    //placing the other tabs maximizes/restores them, which raises each one above the tab that was showing:
    //put that tab back on top, and give it the focus again if the group had it
    member private this.restoreTopTab() =
        if parkedTop.Value <> IntPtr.Zero && this.windows.contains(parkedTop.Value) then
            let top = this.os.windowFromHwnd(parkedTop.Value)
            if top.isMinimized.not then
                top.insertAfter(WindowHandleTypes.HWND_TOP)
                if parkedWasForeground.Value then top.setForeground(false)
                this.saveZorder()

    member private this.cancelSettle() =
        settleTicker.Stop()

    //The apps carry out the requests for their windows (maximize, move, z-order) a little after we made them and each
    //one can push the other tabs between the top tab and its bar, so check again while they finish
    member private this.raiseStripSoon() =
        this.raiseStrip()
        stripChecks.Value |> List.iter (fun d -> d.Dispose())
        stripChecks := [60; 180; 400; 800; 1500; 2500] |> List.map (fun ms ->
            ThreadHelper.cancelablePostBack ms (fun() -> this.invokeAsync <| fun() -> this.raiseStrip()))

    //The tab bar is a window owned by the top tab and has to sit directly above it. Maximizing, restoring, dragging and
    //raising tabs can leave it under the top window, which hides it completely (maximized) or cuts it in half behind
    //whatever window is above (restored). Put it back right above the top tab.
    member private this.raiseStrip() =
        if this.isEmpty.not then
            let top = this.os.windowFromHwnd(zorderCell.value.head)
            if top.isMinimized.not then
                let strip = this.os.windowFromHwnd(this.ts.hwnd)
                let prev = top.prevZorder
                if prev.hwnd <> strip.hwnd then
                    //if the window above the top tab is an always-on-top one, going after it would make the bar always-on-top
                    //too; HWND_TOP puts a normal window above every other normal window and still under the topmost ones
                    if prev.hwnd <> IntPtr.Zero && prev.isTopMost && top.isTopMost.not then
                        strip.insertAfter(WindowHandleTypes.HWND_TOP)
                    else
                        strip.insertAfter(prev)

    //----------------------------------------------------------- docked bar (Groupy style)

    member this.tabCount = this.windows.count

    //the bar has its own row above the windows when the option is on and there is more than one tab
    member this.dockWanted =
        Services.settings.getValue("dockTabBar").cast<bool>() && this.windows.count >= 2

    //the row at the top of the work area that a maximized top window leaves to the bar
    member this.dockRow : Rect option =
        if this.dockWanted && this.isEmpty.not then
            let top = this.os.windowFromHwnd(zorderCell.value.head)
            if top.isMinimized.not && top.isMaximized then
                Mon.fromHwnd(top.hwnd).map(fun mon ->
                    Rect(Pt(mon.workRect.left, mon.workRect.top), Sz(mon.workRect.width, this.tabAppearance.tabHeight)))
            else None
        else None

    //where the visible part of a maximized window goes: the work area, less the bar's row when it is docked
    member private this.dockTarget(hwnd:IntPtr) : Rect option =
        Mon.fromHwnd(hwnd).map <| fun mon ->
            let work = mon.workRect
            if this.dockWanted then
                let h = this.tabAppearance.tabHeight
                Rect(Pt(work.left, work.top + h), Sz(work.width, work.height - h))
            else work

    member private this.dockClose(a:Rect, b:Rect) =
        abs(a.left - b.left) <= 2 && abs(a.top - b.top) <= 2 && abs(a.width - b.width) <= 2 && abs(a.height - b.height) <= 2

    //a maximized window that is already where the docked layout wants it (that is our own doing, not the user's)
    member private this.isDocked(hwnd:IntPtr) : bool =
        if this.dockWanted || dockActive.Value then
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized.not && window.isMaximized then
                match this.dockTarget(hwnd) with
                | Some(target) -> this.dockClose(window.visibleBounds, target)
                | None -> false
            else false
        else false

    //make a maximized window leave the bar its row (or take the whole work area back)
    member private this.dockWindow(hwnd:IntPtr) =
        if this.dockWanted || dockActive.Value then
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized.not && window.isMaximized then
                match this.dockTarget(hwnd) with
                | Some(target) when this.dockClose(window.visibleBounds, target).not ->
                    window.moveVisible(target)
                    dockActive := this.dockWanted
                | _ -> ()

    //the same for the tabs behind the top one, which are placed (maximized) by their own apps a moment later
    member private this.dockLayout() =
        if (this.dockWanted || dockActive.Value) && this.isEmpty.not then
            let top = this.os.windowFromHwnd(zorderCell.value.head)
            if top.isMinimized.not && top.isMaximized then
                this.dockWindow(top.hwnd)
                match this.dockTarget(top.hwnd) with
                | Some(target) when isParked.Value.not ->
                    let w = top.bounds
                    let v = top.visibleBounds
                    let l, t, r, b = v.left - w.left, v.top - w.top, w.right - v.right, w.bottom - v.bottom
                    let rect = Rect(Pt(target.left - l, target.top - t), Sz(target.width + l + r, target.height + t + b))
                    zorderCell.value.tail.where(fun hwnd -> this.os.windowFromHwnd(hwnd).isMinimized.not).iter <| fun hwnd ->
                        this.os.windowFromHwnd(hwnd).moveAsync(rect)
                | _ -> ()
                if this.dockWanted.not then dockActive := false

    //a restored window leaves the bar room above it, if there is none the window is moved down
    member private this.ensureDockRoom() =
        if this.dockWanted && this.isEmpty.not && splitRef.Value.IsNone then
            let top = this.os.windowFromHwnd(zorderCell.value.head)
            if top.isMinimized.not && top.isMaximized.not then
                match Mon.fromHwnd(top.hwnd) with
                | Some(mon) ->
                    let b = top.bounds
                    let work = mon.workRect
                    let needTop = work.top + this.tabAppearance.tabHeight - this.tabAppearance.tabHeightOffset
                    if b.top < needTop && b.top >= work.top - 200 then
                        let height = max 150 (min b.height (work.bottom - needTop))
                        top.move(Rect(Pt(b.left, needTop), Sz(b.width, height)))
                | None -> ()

    member this.refreshDock() = this.invokeAsync <| fun() ->
        this.dockLayout()
        this.ensureDockRoom()
        isMaximizedExport.update()

    //put the other tabs back behind the top window now, if they are parked
    member private this.settleNow() =
        this.cancelSettle()
        if isParked.Value && inMoveSize.value.not then
            isParked := false
            if splitRef.Value.IsSome then
                this.layoutSplit(Some(driverRef.Value))
            elif this.os.windowFromHwnd(zorderCell.value.head).isMinimized.not then
                this.saveTopWindowPlacement()
                this.adjustChildWindows()
                this.restoreTopTab()
                this.ensureDockRoom()
            this.raiseStripSoon()
            this.dockLayout()

    //called for every size/position change of the top window that is not part of a mouse drag
    member private this.touchMotion() =
        this.parkChildren()
        lastMotionTick := Environment.TickCount
        if settleTicker.Enabled.not then settleTicker.Start()

    //park everything except the window being changed (in a split group that is either of the two tiles)
    member private this.hideChildWindows() =
        let keep =
            if splitRef.Value.IsSome && driverRef.Value <> IntPtr.Zero then driverRef.Value
            else zorderCell.value.head
        zorderCell.value.where(fun hwnd -> hwnd <> keep).where(isMinimized >> not).iter(fun window -> this.os.windowFromHwnd(window).hideOffScreen(None))

    member private this.inZorder(windows:List2<IntPtr>) = this.windows.items.sortBy(fun hwnd -> this.os.windowFromHwnd(hwnd).zorder)

    member private this.setZorder(newZorder:List2<_>) =
        if zorderCell.value.list <> newZorder.list then
            prevTop.set(zorderCell.value.tryHead)
            zorderCell.set(newZorder)

    member private this.saveZorder() =
        this.setZorder(this.inZorder(this.windows.items))

    member private this.setWindows(newWindows) =
        windowsCell.set(newWindows)
        this.saveZorder()
        this.updateIsVisible()

    member private this.isEmpty : bool = this.windows.items.isEmpty

    member private this.bringToTop hwnd =
        this.setZorder(zorderCell.value.moveToEnd((=)hwnd))

    member this.isRenamed hwnd = Services.program.getWindowNameOverride(hwnd).IsSome

    member private this.getLastName (text: string) = 
        let separators = [| '\\'; '/' |]
        let parts = text.Split(separators, System.StringSplitOptions.RemoveEmptyEntries)
        if parts.Length > 0 then parts.[parts.Length - 1] else text
    
    member private this.hwndText hwnd = 
        let window = this.os.windowFromHwnd(hwnd)
        let text = Services.program.getWindowNameOverride(hwnd).def(this.getLastName(window.text))
        if System.Diagnostics.Debugger.IsAttached then sprintf "%X - %s" hwnd text else text

    member private this.getTabInfo(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        {
            text = this.hwndText hwnd
            isRenamed = this.isRenamed hwnd
            iconSmall = window.iconSmall
            iconBig = window.iconBig
            preview = fun() ->
                try
                    if window.isMinimized then
                        let size = this.placementBounds.size
                        let icon = window.iconBig
                        let iconSize = icon.Size.Sz
                        let img = Img(size)
                        let g = img.graphics
                        g.FillRectangle(SolidBrush(Color.LightGray), Rect(Pt(), size).Rectangle)
                        g.DrawIcon(icon, ((size.width - iconSize.width).float / 2.0).Int32, ((size.height - iconSize.height).float / 2.0).Int32)
                        img
                    else
                        Img(Win32Helper.PrintWindow(hwnd))
                with ex -> Img(Sz(1, 1))
        }
    
    member private this.setTabInfo(hwnd) =
        this.ts.setTabInfo(Tab(hwnd), this.getTabInfo(hwnd))

    member private this.setTsParent(parentHwnd) =
        this.os.windowFromHwnd(this.ts.hwnd).setParent(this.os.windowFromHwnd(parentHwnd))
        
    member this.isIconOnly 
        with get() = this.ts.isIconOnly
        and set(value) = this.ts.isIconOnly <- value

    member this.hwnd = this.ts.hwnd

    member private this.os : OS = _os
       
    member private this.windowCount = this.windows.count

    member this.placementBounds : Rect = placement.value.map(fst).def(Rect())

    member private this.isTop(hwnd) = zorderCell.value.where(isMinimized >> not).tryHead = Some(hwnd)

    member private this.isTile(hwnd:IntPtr) : bool =
        match splitRef.Value with
        | Some(s) -> s.left = hwnd || s.right = hwnd
        | None -> false

    //a window whose size or position changes move the whole group
    member private this.isDriver(hwnd:IntPtr) : bool = this.isTile(hwnd) || this.isTop(hwnd)

    member this.isSplit = splitRef.Value.IsSome

    member private this.boxOf (rect:Rect) : SplitBox = { l = rect.left; t = rect.top; r = rect.right; b = rect.bottom }
    member private this.rectOf (box:SplitBox) = Rect(Pt(box.l, box.t), Sz(box.w, box.h))

    member private this.saveTopWindowPlacement() =
        //in a split group the placement is the frame, which layoutSplit keeps up to date
        if splitRef.Value.IsNone then this.saveSingleWindowPlacement()

    member private this.saveSingleWindowPlacement() =
        let window = this.os.windowFromHwnd(zorderCell.value.head)
        if  window.isMinimized.not &&
            this.os.isOnScreen(window.bounds)
            then
            let bounds = 
                if window.isMaximized then
                    //windows are placed slightly off screen when maximized, get the bounds of the monitor instead
                    match Mon.fromHwnd(window.hwnd) with
                    | Some(mon) -> mon.workRect.move(-1,-1)
                    | None -> window.bounds
                else window.bounds
            placement.set(Some(bounds, window.placement))
           
    //a tab that is not on show in a split group waits behind the two tiles, at the size of the whole frame
    member private this.placeBehind(hwnd) =
        match placement.value with
        | Some(bounds, _) ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized.not then
                if window.isMaximized then window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
                window.moveAsync(bounds)
        | None -> ()

    member private this.adjustWindowPlacement(hwnd) =
        if splitRef.Value.IsSome then this.placeBehind(hwnd) else this.adjustSingleWindowPlacement(hwnd)

    member private this.adjustSingleWindowPlacement(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        if placement.value.IsSome then
            let bounds,wp = placement.value.Value
            //if you remove this check, then when you drag a window into an Aero Snapp'ed window
            //the dragged in window will be placed at the restore location for the target, instead of
            //at its snapped location - this is because GetWindowPlacement rcNormal is the restore
            //location for snapped windows
            if  wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL &&
                window.placement.showCmd = ShowWindowCommands.SW_SHOWNORMAL
                then
                window.moveAsync(bounds)
            else
                if window.placement.showCmd = ShowWindowCommands.SW_SHOWMINIMIZED then
                    window.setPlacementAsync({wp with showCmd = ShowWindowCommands.SW_SHOWMINIMIZED})
                else
                    if wp.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED then
                        //maximized windows won't move from one monitor to another by setting placement alone,
                        //need to first move to the new bounds, then set placement. This also covers a tab that
                        //was parked off screen un-maximized while the group was dragged: it has to land on the
                        //target monitor first, otherwise it maximizes on the monitor it was parked on
                        window.moveAsync(bounds)
                    window.setPlacementAsync(wp)   
                     
    //Windows' maximize / minimize / restore animation of one window (DWMWA_TRANSITIONS_FORCEDISABLED)
    member private this.setTransitions(hwnd:IntPtr, enabled:bool) =
        try this.os.windowFromHwnd(hwnd).dwmSetAttribute DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED (if enabled then 0 else 1)
        with _ -> ()

    member this.setTabName(hwnd,name) =
        Services.program.setWindowNameOverride(hwnd, name)
        this.setTabInfo(hwnd)

    member this.isMaximized = isMaximizedExport :> ICellOutput<bool>

    member this.isMouseOver = this.ts.isMouseOver

    member this.isDragging = isDraggingExport :> ICellOutput<bool>

    member this.flashTab(tab, flash) =
        flashEvent.Trigger(tab, flash)
        this.ts.setTabBgColor(tab, if flash then Some(this.tabAppearance.tabFlashBgColor) else None)
        
    member this.shellEvents(hwnd, evt) = this.invokeAsync <| fun() ->
        Cell.beginUpdate()
        match evt with
        | ShellEvent.HSHELL_FLASH ->
            //don't flash if its only a single window in the group
            if this.windows.contains(hwnd) &&
               this.windows.count > 1 then
                this.flashTab(Tab(hwnd), true)
        | ShellEvent.HSHELL_REDRAW ->
            if this.windows.contains(hwnd) then
                this.flashTab(Tab(hwnd), false)
        | ShellEvent.HSHELL_WINDOWACTIVATED 
        | ShellEvent.HSHELL_RUDEAPPACTIVATED ->
            if this.windows.contains(hwnd) then
                this.saveZorder()
        | _ -> ()
        Cell.endUpdate()
        

    member this.onEnterMoveSize() =
        inMoveSize.set(true)
        this.cancelSettle()
        this.parkChildren()
        //dragging a maximized window restores it: the bar must stop being the collapsed one of a maximized window
        isMaximizedExport.update()
        this.updateIsVisible()

    member this.onExitMoveSize() =
        inMoveSize.set(false)
        this.cancelSettle()
        isParked := false
        if splitRef.Value.IsSome then
            this.layoutSplit(Some(driverRef.Value))
            this.focusDriverOrTop(driverRef.Value)
        else
            this.saveTopWindowPlacement()
            this.adjustChildWindows()
            this.makeTopWindowForeground()
        isMaximizedExport.update()
        this.ensureDockRoom()
        this.raiseStripSoon()
        this.dockLayout()
        this.updateIsVisible()

    //-------------------------------------------------------------- split view

    //tiles parked while another one is being changed, and tiles we have just placed ourselves, are not the user
    member private this.ignoreLocationChange(hwnd:IntPtr) : bool =
        if this.isTile(hwnd) then
            if isParked.Value && hwnd <> driverRef.Value then true
            else
                let window = this.os.windowFromHwnd(hwnd)
                window.isMinimized.not && window.isMaximized.not &&
                    match expectedTiles.Value.TryFind(hwnd) with
                    | Some(expected) -> SplitLayout.sameBox 2 expected (this.boxOf window.visibleBounds)
                    | None -> false
        else false

    //Works out what the user did to the `driver` tile (nothing, moved, resized, maximized), updates the shared frame
    //to match and puts both tiles, and the tabs waiting behind them, where they belong.
    member private this.layoutSplit(driver:IntPtr option) =
        match splitRef.Value with
        | None -> ()
        | Some(s) ->
            let reserve = this.tabAppearance.tabHeight
            let state =
                match driver with
                | Some(d) when this.isTile(d) ->
                    let window = this.os.windowFromHwnd(d)
                    if window.isMinimized then s.state
                    elif window.isMaximized then
                        //the maximize button of a tile maximizes the whole group; pressing it again restores the group
                        let work = Mon.fromHwnd(d).map(fun mon -> this.boxOf mon.workRect)
                        window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
                        if s.state.maximized then SplitLayout.restore s.state
                        else
                            match work with
                            | Some(work) -> SplitLayout.maximize s.state work
                            | None -> s.state
                    else
                        match expectedTiles.Value.TryFind(d) with
                        | Some(expected) ->
                            let now = this.boxOf window.visibleBounds
                            if SplitLayout.sameBox 2 expected now then s.state
                            elif SplitLayout.isRescale expected now then SplitLayout.rescaled s.state reserve expected now
                            else SplitLayout.userChanged s.state reserve (if s.left = d then SplitLeft else SplitRight) expected now
                        | None -> s.state
                | _ -> s.state
            let leftBox, rightBox = SplitLayout.tiles state reserve
            let leftWindow = this.os.windowFromHwnd(s.left)
            let rightWindow = this.os.windowFromHwnd(s.right)
            let place (window:Window) (box:SplitBox) =
                if window.isMinimized.not then
                    if window.isMaximized then window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
                    window.moveVisible(this.rectOf box)
            place leftWindow leftBox
            place rightWindow rightBox
            //what we really got: a window with a minimum size does not become smaller just because we asked
            expectedTiles :=
                [
                    (if leftWindow.isMinimized then None else Some(s.left, this.boxOf leftWindow.visibleBounds))
                    (if rightWindow.isMinimized then None else Some(s.right, this.boxOf rightWindow.visibleBounds))
                ] |> List.choose id |> Map.ofList
            splitRef := Some({ s with state = state })
            if leftWindow.isMinimized.not && rightWindow.isMinimized.not then
                //the frame the other tabs follow and the tab bar sits above
                let union = leftWindow.bounds.union(rightWindow.bounds)
                placement.set(Some(union, { leftWindow.placement with showCmd = ShowWindowCommands.SW_SHOWNORMAL; rcNormalPosition = union }))
                this.updatePlacements()
                rightWindow.insertAfter(WindowHandleTypes.HWND_TOP)
                leftWindow.insertAfter(WindowHandleTypes.HWND_TOP)
            this.saveZorder()
            //the tiles are never maximized windows, so the bar keeps its own row instead of collapsing over a title bar
            isMaximizedExport.update()
            this.updateIsVisible()

    //show `partner` beside the active tab
    member this.enterSplit(partner:IntPtr) =
        if splitRef.Value.IsNone && this.windows.contains(partner) && this.windows.count > 1 then
            this.settleNow()
            match zorderCell.value.where(isMinimized >> not).tryHead with
            | Some(left) when left <> partner ->
                let leftWindow = this.os.windowFromHwnd(left)
                let state =
                    if leftWindow.isMaximized then
                        let work = Mon.fromHwnd(left).map(fun mon -> this.boxOf mon.workRect).def(this.boxOf leftWindow.visibleBounds)
                        SplitLayout.maximize (SplitLayout.create (this.boxOf leftWindow.placement.rcNormalPosition)) work
                    else SplitLayout.create (this.boxOf leftWindow.visibleBounds)
                splitRef := Some({ left = left; right = partner; state = state })
                expectedTiles := Map.empty
                this.ts.splitTabs <- List2([Tab(left); Tab(partner)])
                this.layoutSplit(None)
                leftWindow.setForeground(false)
            | _ -> ()

    //end the split: one tab (the one with the focus unless told otherwise) takes the whole frame again
    member this.exitSplit(keep:IntPtr option) =
        match splitRef.Value with
        | None -> ()
        | Some(s) ->
            this.settleNow()
            let active =
                match keep with
                | Some(hwnd) -> hwnd
                | None -> if this.os.foreground.hwnd = s.right then s.right else s.left
            let reserve = this.tabAppearance.tabHeight
            splitRef := None
            expectedTiles := Map.empty
            this.ts.splitTabs <- List2()
            let window = this.os.windowFromHwnd(active)
            if window.isMinimized.not then
                if s.state.maximized then
                    //remember the normal size as its restore size, then maximize
                    window.moveVisible(this.rectOf (SplitLayout.tileArea { s.state with maximized = false; frame = s.state.normalFrame } 0))
                    window.showWindow(ShowWindowCommands.SW_SHOWMAXIMIZED)
                else
                    window.moveVisible(this.rectOf (SplitLayout.tileArea s.state reserve))
                window.insertAfter(WindowHandleTypes.HWND_TOP)
                window.setForeground(false)
            this.saveZorder()
            this.saveTopWindowPlacement()
            this.adjustChildWindows()
            isMaximizedExport.update()
            this.updateIsVisible()

    //swap the two sides
    member this.swapSplitSides() =
        match splitRef.Value with
        | Some(s) ->
            splitRef := Some({ s with left = s.right; right = s.left })
            this.ts.splitTabs <- List2([Tab(s.right); Tab(s.left)])
            expectedTiles := Map.empty
            this.layoutSplit(None)
        | None -> ()

    member private this.replaceSplitPane(newHwnd:IntPtr) =
        match splitRef.Value with
        | Some(s) when s.left <> newHwnd && s.right <> newHwnd ->
            let replaceRight = this.os.foreground.hwnd = s.right
            let updated = if replaceRight then { s with right = newHwnd } else { s with left = newHwnd }
            splitRef := Some(updated)
            expectedTiles := Map.empty
            this.ts.splitTabs <- List2([Tab(updated.left); Tab(updated.right)])
            let window = this.os.windowFromHwnd(newHwnd)
            if window.isMinimized then window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
            this.layoutSplit(None)
            window.setForeground(false)
        | _ -> ()

    member this.main(hwnd, evt) = this.invokeAsync <| fun() -> this.mainBody(hwnd, evt)

    //size/position events can arrive faster than they can be handled; queue at most one and let it read the current state
    member this.mainCoalesced(hwnd, evt, pending:bool ref) =
        if pending.Value.not then
            pending := true
            this.invokeAsync <| fun() ->
                pending := false
                this.mainBody(hwnd, evt)

    member private this.mainBody(hwnd, evt) = this.withUpdate <| fun() ->
        match evt with
        | WinEvent.EVENT_SYSTEM_MINIMIZESTART -> 
            if this.windows.contains(hwnd) then
                let needsMinimized = zorderCell.value.any <| fun hwnd -> 
                    this.os.windowFromHwnd(hwnd).isMinimized.not
                if needsMinimized then  
                    this.minimizeAll()
                    this.os.setZorder(zorderCell.value.moveToEnd((=)hwnd))
                this.updateIsVisible()
        //this happens when a window is restored from minimize
        | WinEvent.EVENT_SYSTEM_MINIMIZEEND ->
            if this.windows.contains(hwnd) then
                let needsRestore = zorderCell.value.any <| fun hwnd -> 
                    this.os.windowFromHwnd(hwnd).isMinimized
                if needsRestore then
                    this.restoreAll()
                    this.os.setZorder(zorderCell.value.moveToEnd((=)hwnd))
                    if splitRef.Value.IsSome then this.layoutSplit(None)
                this.updateIsVisible()      
                //foreground status may have changed
                this.foreground <- this.os.foreground.hwnd
        | WinEvent.EVENT_OBJECT_REORDER ->
            this.saveZorder()
        | WinEvent.EVENT_OBJECT_NAMECHANGE ->
            if  this.windows.contains(hwnd) &&
                //some windows (e.g. chrome on GoogleAnalitics page) fire namechange constantly as they are resized
                inMoveSize.value.not 
                then
                this.setTabInfo hwnd
        | WinEvent.EVENT_SYSTEM_MOVESIZESTART ->
            if this.isDriver(hwnd) then
                driverRef := hwnd
                this.onEnterMoveSize()

        | WinEvent.EVENT_SYSTEM_MOVESIZEEND ->
            if this.isDriver(hwnd) then
                driverRef := hwnd
                this.onExitMoveSize()

        //this is here to detect transitions between maximized and
        //restored (both directions). MOVESIZE does not get triggered in this case
        //however, we need to be careful because some apps (Skype.exe) will trigger this
        //event when they loose focus, we don't want to automatically give them focus in this case
        //so make sure that the window HAD focus before reapplying it
        | WinEvent.EVENT_OBJECT_LOCATIONCHANGE ->
            if this.isDriver(hwnd) && inMoveSize.value.not && this.isDocked(hwnd) then
                //the window is where the docked layout put it: nothing to do
                isMaximizedExport.update()
            elif this.isDriver(hwnd) && inMoveSize.value.not && this.ignoreLocationChange(hwnd).not then
                driverRef := hwnd
                //a window that was just maximized gives the bar its row at once, not after the tabs have settled
                this.dockWindow(hwnd)
                //hide the other tabs before doing anything slower, every millisecond they stay visible is seen
                this.parkChildren()
                //you can miss EVENT_SYSTEM_MOVESIZESTART events
                //when a window is created and is immediatly in move size, we subscribe
                //to the event too late (Chrome tab dragging is prime example)
                //could be solved by subscribing only once for MOVESIZESTART gobally for all hwnds
                //but instead, to keep it simple, we just check on all location changes if its in move size
                let window = this.os.windowFromHwnd(hwnd)
                if window.isInMoveSize then
                    this.onEnterMoveSize()
                else
                    let isForeground = this.os.foreground.hwnd = hwnd
                    this.saveTopWindowPlacement()
                    this.touchMotion()
                    if isForeground then
                        this.focusDriverOrTop(hwnd)
                    this.foreground <- this.os.foreground.hwnd
                    isMaximizedExport.update()
                    this.raiseStrip()
            elif this.windows.contains(hwnd) then
                //another tab was placed or maximized behind the top one: it may have landed between the top tab and its bar
                this.raiseStrip()
        | WinEvent.EVENT_SYSTEM_FOREGROUND ->
            this.foreground <- hwnd
            this.saveZorder()
            this.raiseStrip()
        | _ -> ()
      
    member this.addWindow(hwnd, withDelay) = this.withUpdate <| fun() ->
       if this.windows.contains(hwnd).not then
            if withDelay then System.Threading.Thread.Sleep(250)
            let window = this.os.windowFromHwnd(hwnd)
            //every tab is maximized, restored and moved by this group, and each of those plays Windows' own animation
            //for that one window: a stack of ghost outlines and a different tab flashing in front. No animations.
            this.setTransitions(hwnd, false)
            let conflateEvents = Set2(List2([WinEvent.EVENT_SYSTEM_MINIMIZESTART; WinEvent.EVENT_SYSTEM_MINIMIZEEND]))
            let window = this.os.windowFromHwnd(hwnd)
            this.setWindows(this.windows.add hwnd)
            if prevTop.value.IsNone then
                prevTop.set(Some(hwnd))
                this.saveTopWindowPlacement()
            let registerEvent evt =
                let pending = ref false
                let handler =
                    if evt = WinEvent.EVENT_OBJECT_LOCATIONCHANGE then fun() -> this.mainCoalesced(hwnd, evt, pending)
                    else fun() -> this.main(hwnd, evt)
                let handler =
                    if conflateEvents.contains(evt) then Helper.conflate (TimeSpan(0,0,1)) handler
                    else handler
                window.setWinEventHook evt handler
            let hooks = 
                List2([
                    WinEvent.EVENT_OBJECT_NAMECHANGE
                    WinEvent.EVENT_OBJECT_LOCATIONCHANGE
                    WinEvent.EVENT_SYSTEM_MOVESIZESTART
                    WinEvent.EVENT_SYSTEM_MOVESIZEEND
                    WinEvent.EVENT_SYSTEM_MINIMIZESTART
                    WinEvent.EVENT_SYSTEM_MINIMIZEEND
                ]).map(registerEvent)
            let dispose = 
                {
                    new IDisposable with
                        member this.Dispose() = hooks.iter(fun h -> h.Dispose())
                }
            hookCleanup.map(fun hooks -> hooks.add hwnd dispose)
            this.setTabInfo hwnd


            this.ts.addTab(Tab(hwnd))
            this.adjustWindowPlacement(hwnd)
            addedEvent.Trigger(hwnd)
            //test hook: WT_TEST_SPLIT=1 splits the first two tabs of a group as soon as there are two
            if this.windows.count = 2 && Environment.GetEnvironmentVariable("WT_TEST_SPLIT") = "1" then
                this.invokeAsync <| fun() ->
                    //whichever of the two windows is not the active one goes beside it
                    match zorderCell.value.tryHead with
                    | Some(active) -> this.windows.items.tryFind((<>) active).iter(fun other -> this.enterSplit(other))
                    | None -> ()
            //test hook: WT_TEST_NEWTAB=1 asks for a new tab right of the second tab once the group has three windows
            if this.windows.count = 3 && Environment.GetEnvironmentVariable("WT_TEST_NEWTAB") = "1" then
                this.invokeAsync <| fun() ->
                    match this.ts.lorder.tryAt(1) with
                    | Some(Tab(second)) -> Services.program.newTabRightOf(second)
                    | None -> ()

    //a window that was started from a tab's menu: it joins as the tab right of `after` and is the one you see
    member this.addNewTabAfter(hwnd:IntPtr, after:IntPtr) =
        this.addWindow(hwnd, false)
        match this.ts.lorder.tryFindIndex((=) (Tab(after))) with
        | Some(index) -> this.ts.moveTab(Tab(hwnd), index + 1)
        | None -> ()
        if splitRef.Value.IsSome then
            //in a split group the new tab takes the place of the side that has the focus rather than covering both
            this.invokeAsync <| fun() -> this.replaceSplitPane(hwnd)

    member this.removeWindow(hwnd) = this.withUpdate <| fun() ->
        if this.windows.contains(hwnd) then    
            //CASE 777 - chrome windows can close when you merge a single chrome tab
            //into another chrome group, need to exit the move/size and restore windows on screen in this case
            if inMoveSize.value then
                this.onExitMoveSize()
            //closing or moving away one of the two tiles ends the split; the other tab takes the whole frame
            match splitRef.Value with
            | Some(s) when s.left = hwnd || s.right = hwnd ->
                this.exitSplit(Some(if s.left = hwnd then s.right else s.left))
            | _ -> ()
            let window = this.os.windowFromHwnd(hwnd)
            this.setTransitions(hwnd, true)
            this.ts.removeTab(Tab(hwnd))
            this.setWindows(this.windows.remove hwnd)
            hookCleanup.value.find(hwnd).Dispose()
            hookCleanup.map(fun hooks -> hooks.remove(hwnd))
            removedEvent.Trigger(hwnd)
    
    member this.activateIndex(index, force) =
        let nextTab = this.ts.lorder.tryAt(index)
        nextTab.iter <| fun(nextTab) ->
            this.tabActivate(nextTab, force)

    member this.switchWindow(next,force) = 
        if this.windowCount > 1 then
            let lorder = this.ts.lorder
            let max = lorder.count - 1
            let top = zorderCell.value.tryHead
            top.iter <| fun top ->
                (lorder.tryFindIndex((=)(Tab(top)))).iter <| fun index ->
                    let targetIndex = if next then index + 1 else index - 1
                    let targetIndex = 
                        if targetIndex > max then 0
                        elif targetIndex < 0 then max
                        else targetIndex
                    this.activateIndex(targetIndex, force)
                        
    member this.destroy() =
        if isDestroyed.value.not then
            isDestroyed.set(true)
            this.windows.items.iter(fun hwnd -> this.setTransitions(hwnd, true))
            settleTicker.Dispose()
            this.ts.destroy()
            shellHookWindow.value.iter <| fun d -> d.Dispose()
            winEventHandler.value.iter <| fun d -> d.Dispose()
            exitedEvent.Trigger()
            (invoker :> IDisposable).Dispose()

   

    member private this.suppressAnimation f = fun() ->
        let hasAnimation = Win32Helper.GetMinMaxAnimation()
        if hasAnimation then
            Win32Helper.SetMinMaxAnimation(false)
        f()
        if hasAnimation then
            Win32Helper.SetMinMaxAnimation(true)

    //ask every tab at once (each app can take a tenth of a second to react, one after the other that is a visible
    //wave through the group), then wait until they have all done it so the code that follows sees the new state
    member private this.showAllAsync(windows:List2<IntPtr>, cmd:int, isDone:Window -> bool) =
        let pending = windows.map(this.os.windowFromHwnd).where(fun w -> isDone(w).not)
        pending.iter(fun w -> w.showWindowAsync(cmd))
        let deadline = Environment.TickCount + 1000
        while Environment.TickCount < deadline && pending.any(fun w -> isDone(w).not) do
            System.Threading.Thread.Sleep(2)

    member this.minimizeAll = this.suppressAnimation <| fun() ->
        this.showAllAsync(zorderCell.value.reverse, ShowWindowCommands.SW_SHOWMINNOACTIVE, fun w -> w.isMinimized)

    member this.restoreAll = this.suppressAnimation <| fun() ->
        this.showAllAsync(zorderCell.value, ShowWindowCommands.SW_SHOWNOACTIVATE, fun w -> w.isMinimized.not)
        
    member this.tabActivate(Tab(hwnd), force) =
        //activating a tab while the others are still parked would show it where it was parked
        this.settleNow()
        if splitRef.Value.IsSome && this.isTile(hwnd).not then
            //a tab that is not on show takes the place of the tile that has the focus
            this.replaceSplitPane(hwnd)
        else
            let window = this.os.windowFromHwnd(hwnd)
            window.setForegroundOrRestore(force)
            window.bringToTop()

    member this.onTabMoved(hwnd, index) = movedEvent.Trigger(hwnd, index)

    member x.exited = exitedEvent.Publish
    member this.bounds = boundsExport :> ICellOutput<_>
    member this.isForeground = isForegroundExport :> ICellOutput<_>
    member this.zorder = zorderExport :> ICellOutput<_>
    member this.added = addedEvent.Publish
    member this.moved = movedEvent.Publish
    member this.foregroundChanged = foregroundEvent.Publish
    member this.flash = flashEvent.Publish
    member this.removed = removedEvent.Publish
    member this.lorder = this.ts.lorder.map(fun(Tab(hwnd)) -> hwnd)
