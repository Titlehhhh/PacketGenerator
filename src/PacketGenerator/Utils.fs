namespace PacketGenerator.Utils

open System
open System.Text.Json.Nodes
open Protodef
open PacketGenerator.Types

module VersionRange =
    let create ver = { StartVersion = ver; EndVersion = ver }

module Util =
    let equalTwoTypes (t1: ProtodefType option) (t2: ProtodefType option) =
        match t1, t2 with
        | Some t1, Some t2 when Object.ReferenceEquals(t1, t2) -> true
        | Some t1, Some t2 ->
            let json1 = t1.ToJson()
            let json2 = t2.ToJson()

            match JsonNode.Parse(json1), JsonNode.Parse(json2) with
            | null, null -> true
            | node1, node2 when isNull node1 || isNull node2 -> false
            | node1, node2 -> node1.DeepEquals(node2)
        | None, None -> true
        | _ -> false

module Filters =
    let isPacketMapper (s: string) =
        s.Equals("packet", StringComparison.OrdinalIgnoreCase)
    
    let isPacket (s: string) =
        s.StartsWith("packet", StringComparison.OrdinalIgnoreCase) && not (isPacketMapper s)
