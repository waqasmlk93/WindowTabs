namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms
open Microsoft.FSharp.Reflection
open System.Resources
open System.Reflection

type AppearanceProperty = {
    displayText : string
    key: string
    propertyType : AppearancePropertyType
    }

and AppearancePropertyType =
    | HotKeyProperty
    | IntProperty
    | ColorProperty
    | ChoiceProperty of string list    //index of the chosen item, stored in the tab appearance
    | ToggleProperty                   //on/off stored as 0/1 in the tab appearance
    | NumberProperty of int * int      //number with a range, stored in the tab appearance
    | SettingToggle                    //an on/off setting
    | SettingNumber of int * int       //a numeric setting with a range
    | Heading                          //section title, no editor

type AppearanceView() as this =
    let colorConfig key displayText = 
        { displayText=displayText; key=key; propertyType=ColorProperty }

    let intConfig key displayText = 
        { displayText=displayText; key=key; propertyType=IntProperty }
    
    let hkConfig key displayText = 
        { displayText=displayText; key=key; propertyType=HotKeyProperty }

    let choiceConfig key displayText items =
        { displayText=displayText; key=key; propertyType=ChoiceProperty(items) }

    let toggleConfig key displayText =
        { displayText=displayText; key=key; propertyType=ToggleProperty }

    let numberConfig key displayText low high =
        { displayText=displayText; key=key; propertyType=NumberProperty(low, high) }

    let settingToggle key displayText =
        { displayText=displayText; key=key; propertyType=SettingToggle }

    let settingNumber key displayText low high =
        { displayText=displayText; key=key; propertyType=SettingNumber(low, high) }

    let heading displayText =
        { displayText=displayText; key="heading:" + displayText; propertyType=Heading }

    //settings and headings live outside the tab appearance record
    let isAppearanceField prop =
        match prop.propertyType with
        | Heading | SettingToggle | SettingNumber _ -> false
        | _ -> true
        
    let resources = new ResourceManager("Properties.Resources", Assembly.GetExecutingAssembly());
    let font = Font(resources.GetString("Font"), 10f)

    //labels come from the resource file when there is an entry, otherwise the text given here is the label
    let labelText key =
        match resources.GetString(key) with
        | null -> key
        | text -> text

    let properties = List2([
        heading "Tab style"
        choiceConfig "tabStyle" "Style" ["Traditional (slanted tabs)"; "Rounded tabs"]
        toggleConfig "tabBackgroundBar" "Bar behind the tabs"
        choiceConfig "tabCloseButton" "Close button" ["Always shown"; "Only on the tab under the mouse"; "Never"]
        intConfig "tabHeight" "Height"
        intConfig "tabMaxWidth" "Max Width"
        intConfig "tabOverlap" "Overlap"
        intConfig "tabIndentNormal" "Indent Normal"
        intConfig "tabIndentFlipped" "Indent Flipped"
        heading "Colours"
        colorConfig "tabTextColor" "Tab text colour"
        colorConfig "tabNormalBgColor" "Background Normal"
        colorConfig "tabHighlightBgColor" "Background Highlight"
        colorConfig "tabActiveBgColor" "Background Active"
        colorConfig "tabFlashBgColor" "Background Flash"
        colorConfig "tabBorderColor" "Border"
        heading "When the tab bar is visible"
        settingToggle "hideInactiveTabs" "Fade the tab bar of windows that are not active"
        numberConfig "tabInactiveOpacity" "Faded bar opacity (%)" 5 100
        settingToggle "autoHide" "Collapse the bar of a maximized window until the mouse is over it"
        settingToggle "alwaysHideTabs" "Collapse every tab bar until the mouse is over it"
        settingToggle "hideSingleTab" "Hide the bar of a window that has no other tabs"
        heading "Timing"
        settingNumber "tabHideDelayMs" "Delay before a bar collapses (ms)" 1 5000
        settingNumber "tabShowDelayMs" "Delay before a collapsed bar expands (ms)" 0 5000
        settingNumber "tabFadeMs" "Fade time (ms, 0 = instant)" 0 2000
        ])

    let panel = 
        let panel = TableLayoutPanel()
        panel.AutoScroll <- true
        panel.Dock <- DockStyle.Fill
        panel.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        panel.Padding <- Padding(10)
        panel.RowCount <- properties.length + 1
        List2([0..properties.length]).iter <| fun row ->
            panel.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)).ignore
        panel.ColumnCount <- 2
        //labels take what they need, the editors the rest, so nothing runs past the right edge
        panel.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)).ignore
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)).ignore
        panel

    let editors = properties.enumerate.fold (Map2()) <| fun editors (i,prop) ->
        let label =
            let label = Label()
            label.AutoSize <- true
            label.Text <- labelText prop.displayText
            label.TextAlign <- ContentAlignment.MiddleLeft
            label.Font <- font
            label
        match prop.propertyType with
        | Heading ->
            label.Font <- new Font(font, FontStyle.Bold)
            label.Margin <- Padding(0,10,0,0)
            panel.Controls.Add(label)
            panel.SetRow(label, i)
            panel.SetColumn(label, 0)
            panel.SetColumnSpan(label, 2)
            editors
        | _ ->
            let editor =
                match prop.propertyType with
                | ColorProperty -> ColorEditor() :> IPropEditor
                | IntProperty -> IntEditor() :> IPropEditor
                | HotKeyProperty -> HotKeyEditor() :> IPropEditor
                | ChoiceProperty(items) -> ChoiceEditor(items) :> IPropEditor
                | ToggleProperty -> ToggleEditor() :> IPropEditor
                | NumberProperty(low, high) -> NumberEditor(low, high) :> IPropEditor
                | SettingToggle -> BoolEditor() :> IPropEditor
                | SettingNumber(low, high) -> NumberEditor(low, high) :> IPropEditor
                | Heading -> failwith "headings have no editor"

            editor.control.Dock <- DockStyle.Left
            editor.control.Width <- 280
            editor.control.Margin <- Padding(10,5,0,5)
            label.Margin <- Padding(0,5,0,5)
            panel.Controls.Add(label)
            panel.Controls.Add(editor.control)
            panel.SetRow(label, i)
            panel.SetColumn(label, 0)
            panel.SetRow(editor.control, i)
            panel.SetColumn(editor.control, 1)
            editors.add prop.key editor

    let setEditorValues appearance =
        properties.where(isAppearanceField).iter <| fun prop ->
            let editor = editors.find prop.key
            try
                editor.value <- Serialize.readField appearance prop.key
            with | _ ->()

    let appearance = Services.program.tabAppearanceInfo

    let font = Font(resources.GetString("Font"), 9f)

    let buttonPanel =
        let container = new FlowLayoutPanel()
        container.FlowDirection <- FlowDirection.LeftToRight
        container.AutoSize <- true
        container.WrapContents <- false
        container.Anchor <- AnchorStyles.Right

        let resetBtn = Button()
        resetBtn.Text <- resources.GetString("Reset")
        resetBtn.Font <- font
        resetBtn.Click.Add <| fun _ ->
            let appearance = Services.program.defaultTabAppearanceInfo
            setEditorValues appearance
            this.applyAppearance()
        
        let darkBtn = Button()
        darkBtn.Text <- resources.GetString("DarkMode")
        darkBtn.Font <- font
        darkBtn.Click.Add <| fun _ ->
            let appearance = Services.program.darkModeTabAppearanceInfo
            setEditorValues appearance
            this.applyAppearance()

        let darkBlueBtn = Button()
        darkBlueBtn.AutoSize <- true
        
        darkBlueBtn.Text <- resources.GetString("DarkModeBlue")
        darkBlueBtn.Font <- font
        darkBlueBtn.Click.Add <| fun _ ->
            let appearance = Services.program.darkModeBlueTabAppearanceInfo
            setEditorValues appearance
            this.applyAppearance()
        
        container.Controls.Add(darkBtn)
        container.Controls.Add(darkBlueBtn)
        container.Controls.Add(resetBtn)
        container

    do  
        panel.Controls.Add(buttonPanel)
        let btnRow = properties.length
        panel.SetRow(buttonPanel, btnRow)
        panel.SetColumn(buttonPanel, 1)
        setEditorValues appearance
        properties.iter <| fun prop ->
            match prop.propertyType with
            | Heading -> ()
            | _ ->
                let editor = editors.find prop.key
                if isAppearanceField prop then
                    editor.changed.Add <| fun() -> this.applyAppearance()
                else
                    //plain settings: show the current value, write changes straight back
                    editor.value <- Services.settings.getValue(prop.key)
                    editor.changed.Add <| fun() -> Services.settings.setValue(prop.key, editor.value)

    member this.applyAppearance() =
        let appearance = properties.where(isAppearanceField).fold appearance <| fun appearance property ->
            let value = (editors.find property.key).value
            (Serialize.writeField appearance property.key value) :?> TabAppearanceInfo
        Services.settings.setValue("tabAppearance", box(appearance))
        
    interface ISettingsView with
        member x.key = SettingsViewType.AppearanceSettings
        member x.title = resources.GetString("Appearance")
        member x.control = panel :> Control

