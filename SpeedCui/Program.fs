﻿module Program

open System
open Speed
open Speed.Core
open Speed.Brain

module Card =
  let toInt =
    Card.rank >> Rank.toInt

module Console =
  let lock: (unit -> unit) -> unit =
    lock (new obj())

  let readIntLessThanAsync ub =
    let (|ToInt|_|) s =
      match s |> Int32.TryParse with
      | (true, n) -> Some n
      | _ -> None
    let rec loop () =
      async {
        let! line = Console.In.ReadLineAsync() |> Async.AwaitTask
        return
          match line with
          | null -> None
          | ToInt n when n < ub -> Some n
          | _ -> None
      }
    in
      loop ()

module Brain =
  let consoleBrain myId (agent: Post) =

    let body (inbox: Brain) =
      // ゲームの更新通知を処理する
      let rec tryUpdateState waitsAtLeastOne g =
        async {
          let! opt = inbox.TryReceive(10)
          match opt with
          | None ->
              if waitsAtLeastOne
              then return! tryUpdateState true g
              else return Some g
          | Some (ev, g) ->
              match ev with
              | EvGameEnd _ ->
                  return None
              | _ ->
                  return! tryUpdateState false g
        }
      // ユーザの入力を待つ
      let procCommand (g: GameState) =
        async {
          let you = g.PlayerStore |> Map.find myId
          do
            Console.lock (fun ()-> 
              do Console.ForegroundColor <- ConsoleColor.Green
              do printfn "(Which card do you put?)"
              do
                you.Hand
                |> List.iteri (fun i card ->
                    printfn "#%d %d" i (card |> Card.toInt)
                    )
              do Console.ResetColor()
              )
          let! input =
            Console.readIntLessThanAsync (you.Hand |> List.length)
          let evs =
            match input with
            | None ->
                [EvReset]
            | Some i ->
                let card = you.Hand |> Seq.nth i
                in
                  // 場の全枠に置くことを試みる
                  [ for dest in g |> Game.players do
                      yield EvPut (myId, card, dest)
                      ]
          do
            evs |> List.iter (fun ev -> agent.Post(ev))
        }
      let rec loop g =
        async {
          let! opt = tryUpdateState true g
          match opt with
          | None -> return ()
          | Some g ->
              let! () = procCommand g
              return! loop g
        }
      in
        async {
          let! (_, g) = inbox.Receive()
          return! loop g
        }
    in
      MailboxProcessor.Start(body)

  type ConsoleBrain () =
    interface BrainSpec with
      member this.Create(plId, post) =
        consoleBrain plId post

module Audience =
  let consoleAudience =
    { new Audience with
        member this.Listen(g, g', ev) =
          let body () =
            printfn "-------------------------------"
            printfn "Board: %A"
              (g'.Board |> Map.toList |> List.map (snd >> Card.toInt))
            for KeyValue (_, pl) in g'.PlayerStore do
              printfn "Player %s's hand = %A"
                (pl.Name) (pl.Hand |> List.map Card.toInt)

              match ev with
              | EvGameBegin ->
                  printfn "Game start!"

              | EvGameEnd (Win plId as r) ->
                  printfn "Player %s won." ((g' |> Game.player plId).Name)

              | EvPut (plId, card, dest) ->
                  printfn "Player %s puts card %d."
                    ((g' |> Game.player plId).Name)
                    (card |> Card.toInt)

              | EvReset ->
                  printfn "Board reset."
          in
            Console.lock body
    }

[<AutoOpen>]
module Helper =
  let makeEntrant name brain =
    {
      Name = name
      Brain = brain
    }

[<EntryPoint>]
let main argv =

  let ent1 = makeEntrant "You" (Brain.ConsoleBrain())
  let ent2 = makeEntrant "CPU" (NaiveBrain(5000))
  let audience =
    [Audience.consoleAudience]
  in
    Speed.Game.play audience ent1 ent2
    |> Async.RunSynchronously
    |> ignore

  // exit code
  0
