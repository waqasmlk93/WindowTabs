namespace Bemo
open System

type ExceptionHandlerPlugin() as this =

    member this.onException(e:UnhandledExceptionEventArgs) =
        match e.ExceptionObject with
        | :? exn as ex -> ExceptionLog.write "unhandled exception (the program is closing)" ex
        | _ -> ()

    interface IPlugin with
        member x.init() =
            AppDomain.CurrentDomain.UnhandledException.Add this.onException
            //an error on the window thread is logged and the program carries on instead of closing
            System.Windows.Forms.Application.ThreadException.Add <| fun e ->
                ExceptionLog.write "error on the window thread" e.Exception
