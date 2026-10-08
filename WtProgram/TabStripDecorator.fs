namespace Bemo
open System
open System.Drawing
open System.Windows.Forms
open System.Diagnostics
open Bemo.Win32.Forms

type TabStripDecorator(group:WindowGroup) as this =
    let os = OS()
    let Cell = CellScope(false, true)
    let isDraggingCell = Cell.create(false)
    let dragInfoCell = Cell.create(None)
    let dragPtCell = Cell.create(Pt.empty)
    let dropTarget = Cell.create(None)
    let mouseEvent = Event<_>()
    let _ts = TabStrip(this :> ITabStripMonitor)
    //docked bar: the mouse is on the strip of screen where the bar of a window with no other tab opens
    let edgeHoverCell = Cell.create(false)

    do this.init()

    member this.ts = _ts
    member private this.mouse = mouseEvent.Publish
    member private this.init() =
        Services.registerLocal(_ts)
        group.init(this.ts)

        Services.dragDrop.registerTarget(this.ts.hwnd, this:>IDragDropTarget)
    
        dropTarget.set(Some(OleDropTarget(this.ts)))
        
        this.initAutoHide()

        let capturedHwnd = ref None

        this.mouse.Add <| fun(hwnd, btn, action, pt) ->
            match action, btn with
            | MouseDblClick, MouseLeft ->
                group.isIconOnly <- false
                this.beginRename(hwnd)
            | MouseUp, MouseRight ->
                let ptScreen = os.windowFromHwnd(group.hwnd).ptToScreen(pt)
                group.bb.write("contextMenuVisible", true)
                Win32Menu.show group.hwnd ptScreen (this.contextMenu(hwnd))
                group.bb.write("contextMenuVisible", false)
            | MouseDown, _ ->
                capturedHwnd := Some(hwnd)
            | MouseUp, MouseMiddle -> 
                capturedHwnd.Value.iter <| fun capturedHwnd ->
                    if hwnd = capturedHwnd then
                        this.onCloseWindow hwnd
            | _ -> ()
        
        group.bounds.changed.Add <| fun() ->
            this.updateTsPlacement()

        //another program's look can have a different bar height
        group.appearanceChanged.Add <| fun() -> this.updateTsPlacement()

        //the bar of a group with a single tab can be hidden: re-check when tabs come and go or the option changes
        group.added.Add <| fun _ -> this.updateTsPlacement()
        group.removed.Add <| fun _ -> this.updateTsPlacement()
        Services.settings.notifyValue "hideSingleTab" <| fun _ ->
            this.invokeAsync <| fun() -> this.updateTsPlacement()
        //the docked bar: the windows and the bar are laid out again when it is switched on or off, or when tabs come and go
        Services.settings.notifyValue "dockTabBar" <| fun _ ->
            group.refreshDock()
            this.invokeAsync <| fun() -> this.updateTsPlacement()
        group.added.Add <| fun _ -> group.refreshDock()
        group.removed.Add <| fun _ -> group.refreshDock()
        group.appearanceChanged.Add <| fun() -> group.refreshDock()
        group.isMaximized.changed.Add <| fun() -> this.updateTsPlacement()
        this.ts.isMouseOver.changed.Add <| fun() -> this.updateTsPlacement()
        this.initEdgeHover()

        group.exited.Add <| fun() ->
            Services.dragDrop.unregisterTarget(this.ts.hwnd)
    

    member private this.tabSlide =
        dragInfoCell.value.map <| fun dragInfo ->
                dragInfo.tab, dragPtCell.value.sub(dragInfo.tabOffset).x

    member private this.updateTsSlide() =
        this.ts.slide <- this.tabSlide

    member private this.updateTsPlacement() =
      try
        let dock = Services.settings.getValue("dockTabBar").cast<bool>()
        //with the docked bar a window without other tabs keeps a thin bar that opens when the mouse is over it
        let hideSingle = dock.not && Services.settings.getValue("hideSingleTab").cast<bool>() && this.ts.tabs.count <= 1
        //docked bar: nothing is drawn over a window that has no other tab, except while the mouse is at its top edge
        //(while a tab is being dragged every bar is shown, they are where the tab can be dropped to join a group)
        let lonelyHidden = dock && this.ts.tabs.count <= 1 && edgeHoverCell.value.not && this.ts.isMouseOver.value.not && isDraggingCell.value.not
        this.ts.suppressed <- lonelyHidden
        if group.bounds.value.IsNone || hideSingle then
            this.ts.visible <- false
        else
            this.ts.dockedRow <- group.dockRow.IsSome
            match group.dockRow with
            | Some(row) ->
                //a maximized window has been made smaller to leave this row: the bar is full width and has its own place
                this.ts.setPlacement({
                    showInside = false
                    bounds = row
                })
            | None ->
                let normal = this.placement
                if dock && normal.showInside.not && group.topWindow <> IntPtr.Zero then
                    //docked bar above a restored window: exactly as wide as the part of the window you can see (the
                    //window's own rectangle also covers the invisible resize borders) and ending where it begins
                    let v = os.windowFromHwnd(group.topWindow).visibleBounds
                    let h = group.tabAppearance.tabHeight
                    this.ts.setPlacement({ normal with bounds = Rect(Pt(v.left, v.top - h), Sz(v.width, h)) })
                else
                    this.ts.setPlacement(normal)
            this.ts.visible <- true
      with ex -> ExceptionLog.write "updateTsPlacement" ex

    //watches the mouse: over the top edge of a window that has no other tab the bar opens (a timer, because there is no
    //window there to receive the mouse, so nothing sits over the title bar and blocks clicks)
    member private this.initEdgeHover() =
        let pending = ref false
        //a plain background timer (it does not depend on which thread this was built on); the group's state is only
        //touched on the group's own thread
        let onTick = fun (_:obj) ->
          if pending.Value.not then
            pending := true
            group.invokeAsync <| fun() ->
              pending := false
              try
                let dock = Services.settings.getValue("dockTabBar").cast<bool>()
                let inZone =
                    if dock && group.tabCount <= 1 && group.bounds.value.IsSome then
                        let p = Control.MousePosition
                        let bar = this.placement
                        let r = bar.bounds
                        //an open bar over the window: its first few pixels; a bar above the window: the few pixels just above it
                        let zone =
                            if bar.showInside then
                                //a maximized window's frame starts above the screen: the strip to watch is the screen's top edge
                                Rect(Pt(r.x, max r.y (Screen.FromPoint(p).Bounds.Top)), Sz(r.width, 3))
                            else Rect(Pt(r.x, r.y + r.height - 7), Sz(r.width, 7))
                        p.X >= zone.x && p.X < zone.x + zone.width && p.Y >= zone.y && p.Y < zone.y + zone.height
                    else false
                if inZone <> edgeHoverCell.value then
                    edgeHoverCell.value <- inZone
                    this.updateTsPlacement()
              with _ -> ()
        let timer = new System.Threading.Timer(System.Threading.TimerCallback(onTick), null, 200, 70)
        group.exited.Add <| fun() -> timer.Dispose()

    member private this.invokeAsync f = group.invokeAsync f
    member private this.invokeSync f = group.invokeSync f

    member this.placement =
        let decorator =  {
            windowBounds = group.bounds.value.def(Rect())
            monitorBounds = Mon.all.map(fun m -> m.workRect)
            decoratorHeight = group.tabAppearance.tabHeight
            decoratorHeightOffset = group.tabAppearance.tabHeightOffset
            decoratorIndentFlipped = group.tabAppearance.tabIndentFlipped
            decoratorIndentNormal = group.tabAppearance.tabIndentNormal
        }
        {
            showInside = decorator.shouldShowInside
            bounds = decorator.bounds
        }

    member this.beginRename(hwnd) =
        let tab = Tab(hwnd)
        let textBounds = 
            this.ts.sprite.children.pick <| fun (tabOffset, tabSprite) ->
                let tabSprite = tabSprite :?> TabSprite<Tab>
                if tabSprite.id = tab then 
                    Some(Rect(tabSprite.textLocation.add(tabOffset), tabSprite.textSize))
                else None
        let verticalMargin = 2
        let form = new FloatingTextBox()
        form.textBox.Font <- SystemFonts.MenuFont
        form.Location <- textBounds.location.add(this.placement.bounds.location).add(Pt(0, verticalMargin)).Point
        form.SetSize(textBounds.size.add(Sz(0, -2 * verticalMargin)).Size)
        form.textBox.KeyPress.Add <| fun e ->
            if e.KeyChar = char(Keys.Enter) then
                let newName = form.textBox.Text
                group.setTabName(hwnd, if newName.Length = 0 then None else Some(newName))
                form.Close()
            elif e.KeyChar = char(Keys.Escape) then
                form.Close()
        let tabText = this.ts.tabInfo(Tab(hwnd)).text
        form.textBox.Text <- tabText
        form.textBox.SelectionStart <- 0
        form.textBox.SelectionLength <- tabText.Length
        form.textBox.LostFocus.Add <| fun _ ->
            form.Close()
        group.bb.write("renamingTab", true)
        form.Closed.Add <| fun _ ->
            group.bb.write("renamingTab", false)
        form.Show()

    member private this.onCloseWindow hwnd =
        os.windowFromHwnd(hwnd).close()

    member private this.onCloseOtherWindows hwnd =
        group.windows.items.where((<>)hwnd).iter this.onCloseWindow

    member private this.onCloseAllExeWindows exeName =
        let matchesExe hwnd = os.windowFromHwnd(hwnd).pid.exeName = exeName
        group.windows.items.where(matchesExe).iter this.onCloseWindow

    member private this.onCloseAllWindows() =
        group.windows.items.iter this.onCloseWindow

    member private this.contextMenu(hwnd) =
        let checked(isChecked) = if isChecked then List2([MenuFlags.MF_CHECKED]) else List2()
        let grayed(isGrayed) = if isGrayed then List2([MenuFlags.MF_GRAYED]) else List2()
        let iconOnlyItem = CmiRegular({
            text = (if group.isIconOnly then "Expand" else "Shrink") + " tabs"
            image = None
            click = fun() -> group.isIconOnly <- group.isIconOnly.not
            flags = List2()
        })
        let window = os.windowFromHwnd(hwnd)
        let pid = window.pid
        let exeName = pid.exeName
        let processPath = pid.processPath

        let alignmentItem =
            let currentAlignment = this.ts.getAlignment(this.ts.direction)
            let setAlignment newAlignment = fun() -> 
                this.ts.setAlignment(this.ts.direction, newAlignment)

            let alignmentMenuItem(text,alignment) = CmiRegular({
                text = text
                image = None
                flags = checked(currentAlignment = alignment)
                click = setAlignment alignment
            })
            CmiPopUp({
                text = "Align tabs"
                image = None
                items = List2([
                    ("Left", TabLeft)
                    ("Center", TabCenter)
                    ("Right",TabRight)
                ]).map(alignmentMenuItem)
            })

        let autoHideItem =
            let isAutoHideEnabledDef = Services.settings.getValue("autoHide").cast<bool>()
            let isEnabled = group.bb.read("autoHide", isAutoHideEnabledDef)
            CmiRegular({
                text = "Auto hide maximized"
                flags = checked(isEnabled)
                image = None
                click = fun() ->
                    group.bb.write("autoHide", isEnabled.not)
                    //also the saved setting, otherwise the choice is gone the next time WindowTabs starts
                    Services.settings.setValue("autoHide", box(isEnabled.not))
            })

        let newWindowItem =
            CmiRegular({
                text = "New window"
                flags = List2()
                image = None
                click = fun() -> Process.Start(processPath) |> ignore
            })

        //another window of the same program, added to this group as the tab right of this one
        let newTabItem =
            CmiRegular({
                text = "New tab to the right"
                flags = List2()
                image = None
                click = fun() -> Services.program.newTabRightOf(hwnd)
            })

        let combineIconsInTaskbar =
            CmiRegular({
                text = "Combine icons in taskbar"
                image = None
                click = fun() -> Services.desktop.restartGroup(group.hwnd, group.isSuperBarEnabled.not)
                flags = checked(group.isSuperBarEnabled)
            })
        
        let renameTabItem =
            CmiRegular({
                text = "Rename tab"
                image = None
                flags = List2()
                click = fun() ->
                    this.beginRename(hwnd)
            })
        let restoreTabNameItem =
            CmiRegular({
                text = "Restore tab name"
                image = None
                click = fun() -> group.setTabName(hwnd, None)
                flags = List2()
            })

        let removeTabsItem =
            CmiRegular({
                text = sprintf "Remove tabs for '%s' windows" exeName
                image = None
                click = fun() -> Services.filter.setIsTabbingEnabledForProcess processPath false
                flags = List2()
            })

        let isGrouped = Services.program.getAutoGroupingEnabled processPath
        let groupTabsItem =
            CmiRegular({
                text = sprintf "Group tabs for '%s' windows" exeName
                image = None
                click = fun() -> Services.program.setAutoGroupingEnabled processPath isGrouped.not
                flags = checked(isGrouped)
            })
                 
        let closeTabItem = 
            CmiRegular({
                text = "Close"
                image = None
                click = fun() -> this.onCloseWindow hwnd
                flags = List2()
            })

        let closeOtherTabsItem =
            CmiRegular({
                text = "Close others"
                image = None
                click = fun() -> this.onCloseOtherWindows hwnd
                flags = List2()
            })

        let closeAllExeTabsItem =
            CmiRegular({
                text = sprintf "Close all '%s' windows" exeName
                image = None
                click = fun() -> this.onCloseAllExeWindows exeName
                flags = List2()
            })

        let closeAllTabsItem =
            CmiRegular({
                text = "Close all"
                image = None
                click = fun() -> this.onCloseAllWindows()
                flags = List2()
            })

        let managerItem =
            CmiRegular({
                text = "Settings..."
                image = None
                click = fun() -> Services.managerView.show()
                flags = List2()
            })

        //split view: show two tabs side by side inside the one group
        let splitItems =
            let regular text click = CmiRegular({ text = text; image = None; flags = List2(); click = fun() -> group.invokeAsync click })
            if group.isSplit then
                List2([
                    Some(regular "Close split view" (fun() -> group.exitSplit(None)))
                    Some(regular "Swap the two sides" (fun() -> group.swapSplitSides()))
                ])
            elif group.windows.count > 1 then
                if group.topWindow <> hwnd then
                    List2([Some(regular "Show beside the active tab" (fun() -> group.enterSplit(hwnd)))])
                else
                    let others = group.windows.items.where((<>)hwnd)
                    List2([
                        Some(CmiPopUp({
                            text = "Split view with..."
                            image = None
                            items = others.map <| fun other ->
                                regular (this.ts.tabInfo(Tab(other)).text) (fun() -> group.enterSplit(other))
                        }))
                    ])
            else List2()

        List2([
            Some(newWindowItem)
            Some(newTabItem)
            Some(CmiSeparator)
            splitItems.tryAt(0).bind(id)
            splitItems.tryAt(1).bind(id)
            Some(iconOnlyItem)
            Some(alignmentItem)
            Some(autoHideItem)
            Some(combineIconsInTaskbar)
            Some(renameTabItem)
            (if group.isRenamed(hwnd) then Some(restoreTabNameItem) else None)
            Some(CmiSeparator)
            Some(closeTabItem)
            Some(closeOtherTabsItem)
            Some(closeAllExeTabsItem)
            Some(closeAllTabsItem)
            Some(CmiSeparator)
            Some(removeTabsItem)
            Some(groupTabsItem)
            Some(CmiSeparator)
            Some(managerItem)
        ]).choose(id)

    member private this.initAutoHide() =
        let callbackRef = ref None
        let isMaximized = Cell.import(group.isMaximized)
        let isMouseOver = Cell.import(group.isMouseOver)
        let propCell(key,def) =
            let cell = Cell.create(group.bb.read(key, def))
            let update() = cell.value <- group.bb.read(key, def)
            group.bb.subscribe key update
            cell

        let isAutoHideEnabledDef = Services.settings.getValue("autoHide").cast<bool>()
        let autoHideCell = propCell("autoHide", isAutoHideEnabledDef)
        let contextMenuVisibleCell = propCell("contextMenuVisible", false)
        let renamingTabCell = propCell("renamingTab", false)
        //settings that change how and when the bar collapses; bumping this cell re-runs the listener below
        let settingsVersionCell = Cell.create(0)
        let bump(_) = this.invokeAsync <| fun() -> settingsVersionCell.value <- settingsVersionCell.value + 1
        Services.settings.notifyValue "alwaysHideTabs" bump
        Services.settings.notifyValue "dockTabBar" bump
        group.added.Add <| fun _ -> bump(null)
        group.removed.Add <| fun _ -> bump(null)
        Services.settings.notifyValue "tabHideDelayMs" bump
        Services.settings.notifyValue "tabShowDelayMs" bump
        let isRecentlyChangedZorderCell =
            let cell = Cell.create(false)
            let cbRef = ref None
            group.zorder.changed.Add <| fun() ->
                cell.value <- true
                cbRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
                cbRef := Some(ThreadHelper.cancelablePostBack 1000 <| fun() ->
                    cell.value <- false
                )
            cell
        Cell.listen <| fun() ->
            settingsVersionCell.value.ignore
            let alwaysHide = Services.settings.getValue("alwaysHideTabs").cast<bool>()
            let hideDelay = max 1 (Services.settings.getValue("tabHideDelayMs").cast<int>())
            let showDelay = max 0 (Services.settings.getValue("tabShowDelayMs").cast<int>())
            //collapse a maximized window's bar (when auto hide is on), or every bar when 'always hide' is on
            //(docked bar: a window with no other tab keeps a thin bar that opens on hover; with several tabs the bar
            //is always shown in its own row and does not collapse when maximized)
            let dock = Services.settings.getValue("dockTabBar").cast<bool>()
            let shrink = 
                (alwaysHide || (dock.not && isMaximized.value && autoHideCell.value)) &&   
                isMouseOver.value.not && 
                isDraggingCell.value.not &&
                contextMenuVisibleCell.value.not &&
                renamingTabCell.value.not &&
                isRecentlyChangedZorderCell.value.not
            callbackRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
            callbackRef := None
            if shrink then
                callbackRef := Some(ThreadHelper.cancelablePostBack hideDelay <| fun() ->
                    this.ts.isShrunk <- true
                )
            elif showDelay > 0 && this.ts.isShrunk then
                callbackRef := Some(ThreadHelper.cancelablePostBack showDelay <| fun() ->
                    this.ts.isShrunk <- false
                )
            else
                this.ts.isShrunk <- false

    interface ITabStripMonitor with
        member x.tabClick((btn, tab, part, action, pt)) = 
            let (Tab(hwnd)) = tab
            mouseEvent.Trigger(hwnd, btn, action, pt)
            let ptScreen = os.windowFromHwnd(this.ts.hwnd).ptToScreen(pt)
            match action with
            | MouseDown ->
                group.flashTab(tab, false)
                match btn with
                | MouseRight ->
                    os.windowFromHwnd(group.topWindow).setForeground(false)
                | MouseLeft ->
                    group.tabActivate(tab, false)
                    let shiftHeld = (Control.ModifierKeys &&& Keys.Shift) = Keys.Shift
                    let canMove = Services.settings.getValue("tabMoveRequiresShift").cast<bool>().not || shiftHeld
                    if part <> TabClose && canMove then
                        let dragOffset = pt.sub(this.ts.tabLocation tab)
                        let dragImage = fun() -> this.ts.dragImage(tab)
                        let dragInfo = box({ tab = tab; tabOffset = dragOffset; tabInfo = this.ts.tabInfo(tab)})
                        Services.dragDrop.beginDrag(this.ts.hwnd, dragImage, dragOffset, ptScreen, dragInfo)
                | MouseMiddle ->
                    group.tabActivate(tab, false)
            | _ -> ()
    
        member x.tabActivate((tab)) = 
            group.tabActivate(tab, false)
            
        member x.tabMoved(Tab(hwnd), index) =
            group.onTabMoved(hwnd, index)

        member x.tabClose(Tab(hwnd)) =
            //optionally the close button closes the whole group; holding Ctrl closes just this tab
            let ctrlHeld = (Control.ModifierKeys &&& Keys.Control) = Keys.Control
            if Services.settings.getValue("closeButtonClosesGroup").cast<bool>() && ctrlHeld.not then
                this.onCloseAllWindows()
            else
                os.windowFromHwnd(hwnd).close()

        member x.windowMsg(msg) = 
            ()
            
    interface IDragDropTarget with
        member this.dragBegin() =
            this.invokeAsync <| fun() -> 
                isDraggingCell.value <- true
                this.ts.transparent <- false
                this.updateTsPlacement()
                
        member this.dragEnter dragInfo pt =
            this.invokeSync <| fun() -> 
                let dragInfo = dragInfo :?> TabDragInfo
                let (Tab(hwnd)) = dragInfo.tab
                //optionally only windows of the same program may join a group, unless Shift is held
                let allowed =
                    let shiftHeld = (Control.ModifierKeys &&& Keys.Shift) = Keys.Shift
                    if Services.settings.getValue("dropSameAppOnly").cast<bool>().not || shiftHeld then true
                    else
                        let path = os.windowFromHwnd(hwnd).pid.processPath
                        group.windows.count = 0 ||
                        group.windows.items.any(fun other -> os.windowFromHwnd(other).pid.processPath = path)
                let result = 
                    if allowed.not then
                        false
                    elif this.ts.tabs.contains(dragInfo.tab) &&
                        this.ts.tabs.count = 1 then
                        dragPtCell.set(pt)
                        dragInfoCell.set(Some(dragInfo))
                        false
                    else 
                        dragPtCell.set(pt)
                        dragInfoCell.set(Some(dragInfo))
                        this.ts.addTabSlide dragInfo.tab this.tabSlide
                        this.ts.setTabInfo(dragInfo.tab, dragInfo.tabInfo)
                        group.addWindow(hwnd, false)
                        true
                this.updateTsSlide()
                result

        member this.dragMove(pt) =
            this.invokeSync <| fun() ->
                dragPtCell.set(pt)
                this.updateTsSlide()

        member this.dragExit() =
            this.invokeSync <| fun() ->
                match dragInfoCell.value with
                | Some(dragInfo) ->
                    let tab = dragInfo.tab
                    if this.ts.tabs.contains(tab) then
                        this.ts.removeTab(tab)
                        dragInfoCell.set(None)       
                        let (Tab(hwnd)) = tab
                        let window = os.windowFromHwnd(hwnd)
                        group.removeWindow(hwnd)
                        window.hideOffScreen(None)
                | None -> ()
                this.updateTsSlide()

        member this.dragEnd() =
            this.invokeAsync <| fun() ->
                isDraggingCell.value <- false
                this.ts.transparent <- true
                this.updateTsPlacement()
                match this.ts.movedTab with
                | Some(tab, index) ->
                    this.ts.moveTab(tab, index)
                | None -> ()
                dragInfoCell.set(None)
                this.updateTsSlide()
