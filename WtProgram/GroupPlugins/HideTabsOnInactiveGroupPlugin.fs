namespace Bemo
open System

type HideTabsOnInactiveGroupPlugin() =
    let Cell = CellScope()
    let isDragging = Cell.create(false)
    let fadeTimer : IDisposable option ref = ref None

    member this.group = Services.get<WindowGroup>()

    member this.hasGroup = Services.has<WindowGroup>()

    member this.tabStrip = Services.get<TabStrip>()

    member this.shouldShowCompact = 
        this.group.isForeground.value.not &&
        this.group.isMouseOver.value.not &&
        isDragging.value.not &&
        Services.settings.getValue("hideInactiveTabs").cast<bool>()

    //how opaque the bar of an inactive window is: a percentage from the tab appearance, never fully invisible
    //because a bar the mouse can't find can't be hovered to bring it back
    member private this.compactAlpha =
        let percent = this.group.tabAppearance.tabInactiveOpacity
        let percent = if percent < 5 || percent > 100 then 38 else percent
        byte(percent * 255 / 100)

    member private this.fadeTo(target:byte) =
        fadeTimer.Value.iter <| fun d -> d.Dispose()
        fadeTimer := None
        let fadeMs = Services.settings.getValue("tabFadeMs").cast<int>()
        let start = int(this.tabStrip.alpha)
        let target = int(target)
        if fadeMs <= 0 || start = target then
            this.tabStrip.alpha <- byte(target)
        else
            let stepMs = 16
            let steps = max 1 (fadeMs / stepMs)
            let rec step i =
                this.tabStrip.alpha <- byte(start + (target - start) * i / steps)
                if i < steps then
                    fadeTimer := Some(ThreadHelper.cancelablePostBack stepMs (fun() -> step (i + 1)))
            step 1

    member private this.onShowCompactChanged() =
        if this.shouldShowCompact then
            this.fadeTo(this.compactAlpha)
        else
            this.fadeTo(byte(0xFF))

    interface IDragDropNotification with
        member this.dragBegin() = this.group.invokeAsync <| fun() ->
            isDragging.set(true)

        member this.dragEnd() = this.group.invokeAsync <| fun() ->
            isDragging.set(false)

    interface IPlugin with
        member this.init() =
            Services.dragDrop.registerNotification(this :> IDragDropNotification)
            let onSettingChanged = fun(_) ->
                if this.hasGroup then
                    this.group.invokeAsync <| fun() ->
                        this.onShowCompactChanged()
            Services.settings.notifyValue "hideInactiveTabs" onSettingChanged
            Services.settings.notifyValue "tabAppearance" onSettingChanged
            Services.settings.notifyValue "tabFadeMs" onSettingChanged
            this.group.zorder.changed.Add <| fun() ->
                this.onShowCompactChanged()
            this.group.isForeground.changed.Add <| fun() ->
                this.onShowCompactChanged()
            this.group.isMouseOver.changed.Add <| fun() ->
                this.onShowCompactChanged()
            Cell.listen <| fun() ->
                isDragging.value.ignore
                this.onShowCompactChanged()