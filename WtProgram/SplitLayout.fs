namespace Bemo
open System

//Split view: two windows of a group shown side by side inside one shared frame. This file is only the maths; it knows
//nothing about windows. Boxes are edges (left, top, right, bottom) of the VISIBLE part of a window, so two tiles that
//touch really touch.

type SplitPane =
    | SplitLeft
    | SplitRight

type SplitBox = {
    l: int
    t: int
    r: int
    b: int
    } with
    member this.w = this.r - this.l
    member this.h = this.b - this.t

type SplitState = {
    //the area the group occupies; when maximized it is a monitor's work area including the row for the tab bar
    frame: SplitBox
    //share of the frame's width taken by the left tile
    ratio: float
    maximized: bool
    //the frame to go back to when a maximized group is restored
    normalFrame: SplitBox
    }

module SplitLayout =
    //a tile never gets narrower than this unless the frame itself is narrower than two of them
    let minTile = 160

    let clampRatio (width:int) (ratio:float) =
        if width < 2 * minTile then 0.5
        else
            let low = float minTile / float width
            max low (min (1.0 - low) ratio)

    let create (frame:SplitBox) = {
        frame = frame
        ratio = 0.5
        maximized = false
        normalFrame = frame
        }

    //the area the tiles share: the frame minus the row the tab bar needs when the frame is a maximized work area
    let tileArea (s:SplitState) (reserve:int) =
        if s.maximized then { s.frame with t = s.frame.t + reserve } else s.frame

    //x position of the divider between the tiles
    let divider (s:SplitState) (reserve:int) =
        let area = tileArea s reserve
        area.l + int (Math.Round(s.ratio * float area.w))

    //visible bounds of the left and right tile
    let tiles (s:SplitState) (reserve:int) =
        let area = tileArea s reserve
        let x = divider s reserve
        { area with r = x }, { area with l = x }

    let private shift (box:SplitBox) dx dy = { l = box.l + dx; t = box.t + dy; r = box.r + dx; b = box.b + dy }

    //maximizing the group: it takes the whole work area, remembering where it was
    let maximize (s:SplitState) (work:SplitBox) =
        { s with normalFrame = (if s.maximized then s.normalFrame else s.frame); frame = work; maximized = true }

    let restore (s:SplitState) =
        { s with frame = s.normalFrame; maximized = false }

    //The user moved or resized the tile of `pane`, from `oldTile` to `newTile`. A move carries the whole group along;
    //dragging the edge two tiles share moves the divider; dragging any outer edge resizes the group.
    let userChanged (s:SplitState) (reserve:int) (pane:SplitPane) (oldTile:SplitBox) (newTile:SplitBox) =
        //leaving the maximized state: from here on the frame is exactly what the tiles cover
        let frame = tileArea s reserve
        if newTile.w = oldTile.w && newTile.h = oldTile.h then
            let moved = shift frame (newTile.l - oldTile.l) (newTile.t - oldTile.t)
            { s with frame = moved; normalFrame = moved; maximized = false }
        else
            let top = frame.t + (newTile.t - oldTile.t)
            let bottom = frame.b + (newTile.b - oldTile.b)
            let left, right, dividerX =
                match pane with
                | SplitLeft -> frame.l + (newTile.l - oldTile.l), frame.r, newTile.r
                | SplitRight -> frame.l, frame.r + (newTile.r - oldTile.r), newTile.l
            let resized = { l = left; t = top; r = right; b = bottom }
            let ratio = clampRatio resized.w (float (dividerX - left) / float (max 1 resized.w))
            { frame = resized; ratio = ratio; maximized = false; normalFrame = resized }

    //A tile that changes size by the same factor in both directions while it moves has been carried to a monitor with
    //different scaling (a user resize hardly ever lands on exactly the same factor). The whole frame moves with it and
    //is scaled by the same factor, so the group keeps its proportions.
    let isRescale (oldTile:SplitBox) (newTile:SplitBox) =
        let sx = float newTile.w / float (max 1 oldTile.w)
        let sy = float newTile.h / float (max 1 oldTile.h)
        abs (sx - sy) < 0.01 && abs (sx - 1.0) > 0.03

    let rescaled (s:SplitState) (reserve:int) (oldTile:SplitBox) (newTile:SplitBox) =
        let frame = tileArea s reserve
        let k = float newTile.h / float (max 1 oldTile.h)
        let scale (v:int) = int (Math.Round(float v * k))
        let l = newTile.l - scale (oldTile.l - frame.l)
        let t = newTile.t - scale (oldTile.t - frame.t)
        let moved = { l = l; t = t; r = l + scale frame.w; b = t + scale frame.h }
        { s with frame = moved; normalFrame = moved; maximized = false }

    //two boxes that differ by no more than `tolerance` pixels on every edge are the same place
    let sameBox (tolerance:int) (a:SplitBox) (b:SplitBox) =
        abs (a.l - b.l) <= tolerance && abs (a.t - b.t) <= tolerance && abs (a.r - b.r) <= tolerance && abs (a.b - b.b) <= tolerance
