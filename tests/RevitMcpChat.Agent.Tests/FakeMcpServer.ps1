param([string]$ToolName = "revit_fake_test")

while ($null -ne ($line = [Console]::In.ReadLine())) {
    $request = ConvertFrom-Json -InputObject $line
    if ($request.method -eq "server/discover") {
        $response = @{
            jsonrpc = "2.0"
            id = $request.id
            error = @{ code = -32601; message = "Method not found" }
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $response -Compress -Depth 30))
        [Console]::Out.Flush()
    }
    elseif ($request.method -eq "initialize") {
        $response = @{
            jsonrpc = "2.0"
            id = $request.id
            result = @{
                protocolVersion = $request.params.protocolVersion
                capabilities = @{ tools = @{} }
                serverInfo = @{ name = "fake-rvt-mcp"; version = "1.0.0" }
            }
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $response -Compress -Depth 30))
        [Console]::Out.Flush()
    }
    elseif ($request.method -eq "tools/list") {
        $response = @{
            jsonrpc = "2.0"
            id = $request.id
            result = @{
                tools = @(
                    @{
                        name = $ToolName
                        description = "Fake MCP tool for transport tests"
                        inputSchema = @{
                            type = "object"
                            properties = @{}
                            additionalProperties = $false
                        }
                    }
                )
            }
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $response -Compress -Depth 30))
        [Console]::Out.Flush()
    }
    elseif ($request.method -eq "tools/call") {
        $response = @{
            jsonrpc = "2.0"
            id = $request.id
            result = @{
                content = @(@{ type = "text"; text = "TARGET_LOST" })
                isError = $true
            }
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $response -Compress -Depth 30))
        [Console]::Out.Flush()
    }
}

