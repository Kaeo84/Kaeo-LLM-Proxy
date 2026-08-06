# Dashboard Scrolling, MCP Stats Trim, Accept-Loop Exception Fix

Date: 2026-08-06

## Understanding
Three fixes: (1) the Dashboard tab content overflows the fixed-size form and must scroll; (2) the MCP stats panel should not show Prompt/Completion token counters (MCP is not a model endpoint) but keeps Total Requests, Errors, Req/s, Reset; (3) an unobserved-task exception ("The I/O operation has been aborted...") surfaces when the MCP listener stops while GetContextAsync is pending — the accept loop must observe the abandoned task like ProxyServer does.

## Assumptions
- TableLayoutPanel AutoScroll does not produce scrollbars reliably; the standard fix is an AutoScroll Panel hosting an AutoSize, Dock=Top TLP.
- The percent filler row in _tlpDashboard contributes zero height under AutoSize and can stay.
- The unobserved exception originates from McpServerHost.AcceptLoopAsync's abandoned GetContextAsync task on stop/restart.

## Steps
- [x] 1. Save plan copy to Plans folder
- [x] 2. Designer: wrap _tlpDashboard in _dashScrollPanel (AutoScroll) with Dock=Top/AutoSize TLP
- [x] 3. Designer: remove MCP Prompt/Completion token labels and move Req/s row up
- [x] 4. MainForm: drop token counters from RefreshMcpStats
- [x] 5. McpServerHost: observe abandoned GetContextAsync task in AcceptLoopAsync
- [x] 6. Build and fix compile errors
- [x] 7. Git commit
