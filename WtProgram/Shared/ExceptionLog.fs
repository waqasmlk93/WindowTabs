namespace Bemo
open System

//errors that would have closed WindowTabs are written next to the program and the program keeps running
module ExceptionLog =
    let write (where:string) (ex:exn) =
        try
            let path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowTabs.errors.log")
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + Environment.NewLine + ex.ToString() + Environment.NewLine + Environment.NewLine)
        with _ -> ()
