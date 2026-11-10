import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerSetDoorSwingTool(server: McpServer) {
  server.tool(
    "set_door_swing",
    `Work out each door's swing / handing from its plan swing arc and write it to a text parameter (default 'Swing'), creating the parameter on Doors if needed. Values: Left, Right (hinge side seen from the push side), Pair, Double acting, None.

GUIDANCE:
- Use before a door schedule that needs swing / handing / opening direction ("כיוון פתיחה"): call this, then add the 'Swing' field in create_schedule.
- Revit has no built-in swing parameter; this fills one deterministically from geometry. Safe to re-run.`,
    {
      parameterName: z.string().optional().describe("Text parameter to write. Default: 'Swing' (created if missing)."),
      doorIds: z.array(z.number()).optional().describe("Only these doors. Default: all doors."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_door_swing", {
            parameterName: args.parameterName ?? "Swing",
            doorIds: args.doorIds ?? [],
          });
        });
        return rawToolResponse("set_door_swing", response);
      } catch (error) {
        return rawToolError("set_door_swing", `Set door swing failed: ${errorMessage(error)}`);
      }
    }
  );
}
