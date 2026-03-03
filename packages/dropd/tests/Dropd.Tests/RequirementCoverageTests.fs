module Dropd.Tests.RequirementCoverageTests

open Expecto

[<Tests>]
let tests =
    testList
        "Requirement Coverage"
        [

          testCase "Catalog contains exactly 89 requirement IDs"
          <| fun _ ->
              Expect.equal
                  RequirementCatalog.allRequirementIds.Length
                  89
                  "should have exactly 89 DD IDs"

          testCase "First ID is DD-001 and last is DD-089 after sorting"
          <| fun _ ->
              let sorted = RequirementCatalog.allRequirementIds |> List.sort
              Expect.equal (List.head sorted) "DD-001" "first after sort"
              Expect.equal (List.last sorted) "DD-089" "last after sort"

          testCase "No duplicate requirement IDs"
          <| fun _ ->
              let distinct =
                  RequirementCatalog.allRequirementIds |> List.distinct

              Expect.equal
                  distinct.Length
                  RequirementCatalog.allRequirementIds.Length
                  "all IDs should be unique"

          ]
