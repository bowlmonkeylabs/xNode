using System;
using System.Collections.Generic;
using System.Linq;
using Sirenix.Utilities;
using UnityEditor;
using UnityEngine;
using XNode;
using XNodeEditor.Internal;

namespace XNodeEditor {
    /// <summary> Contains GUI methods </summary>
    public partial class NodeEditorWindow {
        public NodeGraphEditor graphEditor;
        private List<UnityEngine.Object> selectionCache;
        private List<XNode.Node> culledNodes;
        /// <summary> 19 if docked, 22 if not </summary>
        private int topPadding { get { return isDocked() ? 19 : 22; } }
        /// <summary> Executed after all other window GUI. Useful if Zoom is ruining your day. Automatically resets after being run.</summary>
        public event Action onLateGUI;
        private static readonly Vector3[] polyLineTempArray = new Vector3[2];

        private List<Node> selectedNodes = new List<Node>();
        private float minimapZoomMinFactor = 20f;
        private float minimapZoomMaxFactor = 80f;
        private float minMinimapSize = 100f;
        private Rect miniMapRect;
        private Vector2 boundsToMinimapFactor;
        private Vector2 miniMapCenter;
        private bool isDraggingMinimap;
        private bool isResizingMinimap;
        private Vector2 mouseDownPos;
        private Vector2 prevPanOffset;

        protected virtual void OnGUI() {
            Event e = Event.current;
            Matrix4x4 m = GUI.matrix;
            if (graph == null) return;
            ValidateGraphEditor();
            Controls();

            DrawGrid(position, zoom, panOffset);
            DrawConnections();
            DrawDraggedConnection();
            DrawNodes();
            DrawMiniMap();
            DrawNodesMinimap();
            HandleMinimapEvents();
            DrawSelectionBox();
            DrawTooltip();
            graphEditor.OnGUI();

            // Run and reset onLateGUI
            if (onLateGUI != null) {
                onLateGUI();
                onLateGUI = null;
            }

            //Show name of graph currently being edited.
            if ( NodeEditorWindow.current )
            {
                GUIStyle myStyle = new GUIStyle();
                myStyle.fontSize = 30;
                myStyle.normal.textColor = Color.white;
                GUILayout.Label(NodeEditorWindow.current.graph.name, myStyle);
            }

            GUI.matrix = m;
        }

        public static void BeginZoomed(Rect rect, float zoom, float topPadding) {
            GUI.EndClip();

            GUIUtility.ScaleAroundPivot(Vector2.one / zoom, rect.size * 0.5f);
            Vector4 padding = new Vector4(0, topPadding, 0, 0);
            padding *= zoom;
            GUI.BeginClip(new Rect(-((rect.width * zoom) - rect.width) * 0.5f, -(((rect.height * zoom) - rect.height) * 0.5f) + (topPadding * zoom),
                rect.width * zoom,
                rect.height * zoom));
        }

        public static void EndZoomed(Rect rect, float zoom, float topPadding) {
            GUIUtility.ScaleAroundPivot(Vector2.one * zoom, rect.size * 0.5f);
            Vector3 offset = new Vector3(
                (((rect.width * zoom) - rect.width) * 0.5f),
                (((rect.height * zoom) - rect.height) * 0.5f) + (-topPadding * zoom) + topPadding,
                0);
            GUI.matrix = Matrix4x4.TRS(offset, Quaternion.identity, Vector3.one);
        }

        public void DrawGrid(Rect rect, float zoom, Vector2 panOffset) {

            rect.position = Vector2.zero;

            Vector2 center = rect.size / 2f;
            Texture2D gridTex = graphEditor.GetGridTexture();
            Texture2D crossTex = graphEditor.GetSecondaryGridTexture();

            // Offset from origin in tile units
            float xOffset = -(center.x * zoom + panOffset.x) / gridTex.width;
            float yOffset = ((center.y - rect.size.y) * zoom + panOffset.y) / gridTex.height;

            Vector2 tileOffset = new Vector2(xOffset, yOffset);

            // Amount of tiles
            float tileAmountX = Mathf.Round(rect.size.x * zoom) / gridTex.width;
            float tileAmountY = Mathf.Round(rect.size.y * zoom) / gridTex.height;

            Vector2 tileAmount = new Vector2(tileAmountX, tileAmountY);

            // Draw tiled background
            GUI.DrawTextureWithTexCoords(rect, gridTex, new Rect(tileOffset, tileAmount));
            GUI.DrawTextureWithTexCoords(rect, crossTex, new Rect(tileOffset + new Vector2(0.5f, 0.5f), tileAmount));
        }

        public void DrawSelectionBox() {
            if (currentActivity == NodeActivity.DragGrid) {
                Vector2 curPos = WindowToGridPosition(Event.current.mousePosition);
                Vector2 size = curPos - dragBoxStart;
                Rect r = new Rect(dragBoxStart, size);
                r.position = GridToWindowPosition(r.position);
                r.size /= zoom;
                Handles.DrawSolidRectangleWithOutline(r, new Color(0, 0, 0, 0.1f), new Color(1, 1, 1, 0.6f));
            }
        }

        public static bool DropdownButton(string name, float width) {
            return GUILayout.Button(name, EditorStyles.toolbarDropDown, GUILayout.Width(width));
        }

        /// <summary> Show right-click context menu for hovered reroute </summary>
        void ShowRerouteContextMenu(RerouteReference reroute) {
            GenericMenu contextMenu = new GenericMenu();
            contextMenu.AddItem(new GUIContent("Remove"), false, () => reroute.RemovePoint());
            contextMenu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
        }

        /// <summary> Show right-click context menu for hovered port </summary>
        void ShowPortContextMenu(XNode.NodePort hoveredPort) {
            GenericMenu contextMenu = new GenericMenu();
            foreach (var port in hoveredPort.GetConnections()) {
                var name = port.node.name;
                var index = hoveredPort.GetConnectionIndex(port);
                contextMenu.AddItem(new GUIContent(string.Format("Disconnect({0})", name)), false, () => hoveredPort.Disconnect(index));
            }
            contextMenu.AddItem(new GUIContent("Clear Connections"), false, () => hoveredPort.ClearConnections());
            //Get compatible nodes with this port
            if (NodeEditorPreferences.GetSettings().createFilter) {
                contextMenu.AddSeparator("");

                if (hoveredPort.direction == XNode.NodePort.IO.Input)
                    graphEditor.AddContextMenuItems(contextMenu, hoveredPort.ValueType, XNode.NodePort.IO.Output);
                else
                    graphEditor.AddContextMenuItems(contextMenu, hoveredPort.ValueType, XNode.NodePort.IO.Input);
            }
            contextMenu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
        }

        static Vector2 CalculateBezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t) {
            float u = 1 - t;
            float tt = t * t, uu = u * u;
            float uuu = uu * u, ttt = tt * t;
            return new Vector2(
                (uuu * p0.x) + (3 * uu * t * p1.x) + (3 * u * tt * p2.x) + (ttt * p3.x),
                (uuu * p0.y) + (3 * uu * t * p1.y) + (3 * u * tt * p2.y) + (ttt * p3.y)
            );
        }

        /// <summary> Draws a line segment without allocating temporary arrays </summary>
        static void DrawAAPolyLineNonAlloc(float thickness, Vector2 p0, Vector2 p1) {
            polyLineTempArray[0].x = p0.x;
            polyLineTempArray[0].y = p0.y;
            polyLineTempArray[1].x = p1.x;
            polyLineTempArray[1].y = p1.y;
            Handles.DrawAAPolyLine(thickness, polyLineTempArray);
        }

        /// <summary> Draw a bezier from output to input in grid coordinates </summary>
        public void DrawNoodle(Gradient gradient, NoodlePath path, NoodleStroke stroke, float thickness, List<Vector2> gridPoints) {
            // convert grid points to window points
            for (int i = 0; i < gridPoints.Count; ++i)
                gridPoints[i] = GridToWindowPosition(gridPoints[i]);

            Color originalHandlesColor = Handles.color;
            Handles.color = gradient.Evaluate(0f);
            int length = gridPoints.Count;
            switch (path) {
                case NoodlePath.Curvy:
                    Vector2 outputTangent = Vector2.right;
                    for (int i = 0; i < length - 1; i++) {
                        Vector2 inputTangent;
                        // Cached most variables that repeat themselves here to avoid so many indexer calls :p
                        Vector2 point_a = gridPoints[i];
                        Vector2 point_b = gridPoints[i + 1];
                        float dist_ab = Vector2.Distance(point_a, point_b);
                        if (i == 0) outputTangent = zoom * dist_ab * 0.01f * Vector2.right;
                        if (i < length - 2) {
                            Vector2 point_c = gridPoints[i + 2];
                            Vector2 ab = (point_b - point_a).normalized;
                            Vector2 cb = (point_b - point_c).normalized;
                            Vector2 ac = (point_c - point_a).normalized;
                            Vector2 p = (ab + cb) * 0.5f;
                            float tangentLength = (dist_ab + Vector2.Distance(point_b, point_c)) * 0.005f * zoom;
                            float side = ((ac.x * (point_b.y - point_a.y)) - (ac.y * (point_b.x - point_a.x)));

                            p = tangentLength * Mathf.Sign(side) * new Vector2(-p.y, p.x);
                            inputTangent = p;
                        } else {
                            inputTangent = zoom * dist_ab * 0.01f * Vector2.left;
                        }

                        // Calculates the tangents for the bezier's curves.
                        float zoomCoef = 50 / zoom;
                        Vector2 tangent_a = point_a + outputTangent * zoomCoef;
                        Vector2 tangent_b = point_b + inputTangent * zoomCoef;
                        // Hover effect.
                        int division = Mathf.RoundToInt(.2f * dist_ab) + 3;
                        // Coloring and bezier drawing.
                        int draw = 0;
                        Vector2 bezierPrevious = point_a;
                        for (int j = 1; j <= division; ++j) {
                            if (stroke == NoodleStroke.Dashed) {
                                draw++;
                                if (draw >= 2) draw = -2;
                                if (draw < 0) continue;
                                if (draw == 0) bezierPrevious = CalculateBezierPoint(point_a, tangent_a, tangent_b, point_b, (j - 1f) / (float) division);
                            }
                            if (i == length - 2)
                                Handles.color = gradient.Evaluate((j + 1f) / division);
                            Vector2 bezierNext = CalculateBezierPoint(point_a, tangent_a, tangent_b, point_b, j / (float) division);
                            DrawAAPolyLineNonAlloc(thickness, bezierPrevious, bezierNext);
                            bezierPrevious = bezierNext;
                        }
                        outputTangent = -inputTangent;
                    }
                    break;
                case NoodlePath.CurvyArrows:
                    Vector2 outputedTangent = Vector2.right;
                    for (int i = 0; i < length - 1; i++) {
                        Vector2 inputTangent;
                        // Cached most variables that repeat themselves here to avoid so many indexer calls :p
                        Vector2 point_a = gridPoints[i];
                        Vector2 point_b = gridPoints[i + 1];
                        float dist_ab = Vector2.Distance(point_a, point_b);
                        if (i == 0) outputedTangent = zoom * dist_ab * 0.01f * Vector2.right;
                        if (i < length - 2) {
                            Vector2 point_c = gridPoints[i + 2];
                            Vector2 ab = (point_b - point_a).normalized;
                            Vector2 cb = (point_b - point_c).normalized;
                            Vector2 ac = (point_c - point_a).normalized;
                            Vector2 p = (ab + cb) * 0.5f;
                            float tangentLength = (dist_ab + Vector2.Distance(point_b, point_c)) * 0.005f * zoom;
                            float side = ((ac.x * (point_b.y - point_a.y)) - (ac.y * (point_b.x - point_a.x)));

                            p = tangentLength * Mathf.Sign(side) * new Vector2(-p.y, p.x);
                            inputTangent = p;
                        } else {
                            inputTangent = zoom * dist_ab * 0.01f * Vector2.left;
                        }

                        // Calculates the tangents for the bezier's curves.
                        float zoomCoef = 50 / zoom;
                        Vector2 tangent_a = point_a + outputedTangent * zoomCoef;
                        Vector2 tangent_b = point_b + inputTangent * zoomCoef;
                        // Hover effect.
                        int division = Mathf.RoundToInt(.2f * dist_ab) + 3;
                        // Coloring and bezier drawing.
                        int draw = 0;
                        Vector2 bezierPrevious = point_a;
                        for (int j = 1; j <= division; ++j) {
                            if (stroke == NoodleStroke.Dashed) {
                                draw++;
                                if (draw >= 2) draw = -2;
                                if (draw < 0) continue;
                                if (draw == 0) bezierPrevious = CalculateBezierPoint(point_a, tangent_a, tangent_b, point_b, (j - 1f) / (float) division);
                            }
                            if (i == length - 2)
                                Handles.color = gradient.Evaluate((j + 1f) / division);
                            Vector2 bezierNext = CalculateBezierPoint(point_a, tangent_a, tangent_b, point_b, j / (float) division);
                            DrawAAPolyLineNonAlloc(thickness, bezierPrevious, bezierNext);

                            //Draw Arrows
                            if (j == division)
                            {
                                //Vector2 forward = tangent_b.normalized;
                                Vector2 forward = (bezierNext - bezierPrevious).normalized;
                                Vector2 right = Vector2.Perpendicular(forward);
                                float arrowBackOffset = 5f / zoom;
                                float arrowWidth = 12.5f / zoom;
                                float arrowLength = 15f / zoom;
                                DrawAAPolyLineNonAlloc(thickness, point_b - arrowBackOffset * forward, point_b - forward*(arrowLength + arrowBackOffset) + right * arrowWidth);
                                DrawAAPolyLineNonAlloc(thickness, point_b - arrowBackOffset * forward, point_b - forward*(arrowLength + arrowBackOffset)  - right * arrowWidth);
                            }


                            bezierPrevious = bezierNext;
                        }

                        outputedTangent = -inputTangent;
                    }
                    break;
                case NoodlePath.Straight:
                    for (int i = 0; i < length - 1; i++) {
                        Vector2 point_a = gridPoints[i];
                        Vector2 point_b = gridPoints[i + 1];
                        // Draws the line with the coloring.
                        Vector2 prev_point = point_a;
                        // Approximately one segment per 5 pixels
                        int segments = (int) Vector2.Distance(point_a, point_b) / 5;
                        segments = Math.Max(segments, 1);

                        int draw = 0;
                        for (int j = 0; j <= segments; j++) {
                            draw++;
                            float t = j / (float) segments;
                            Vector2 lerp = Vector2.Lerp(point_a, point_b, t);
                            if (draw > 0) {
                                if (i == length - 2) Handles.color = gradient.Evaluate(t);
                                DrawAAPolyLineNonAlloc(thickness, prev_point, lerp);
                            }
                            prev_point = lerp;
                            if (stroke == NoodleStroke.Dashed && draw >= 2) draw = -2;
                        }
                    }
                    break;
                case NoodlePath.Arrows:
                    for (int i = 0; i < length - 1; i++) {
                        Vector2 point_a = gridPoints[i];
                        Vector2 point_b = gridPoints[i + 1];
                        // Draws the line with the coloring.
                        Vector2 prev_point = point_a;
                        // Approximately one segment per 5 pixels
                        int segments = (int) Vector2.Distance(point_a, point_b) / 5;
                        segments = Math.Max(segments, 1);

                        int draw = 0;
                        for (int j = 0; j <= segments; j++) {
                            draw++;
                            float t = j / (float) segments;
                            Vector2 lerp = Vector2.Lerp(point_a, point_b, t);
                            if (draw > 0) {
                                if (i == length - 2) Handles.color = gradient.Evaluate(t);
                                DrawAAPolyLineNonAlloc(thickness, prev_point, lerp);
                            }
                            //Draw Arrows, every 5th segment
                            if (j == segments)
                            {
                                Vector2 forward = lerp - prev_point;
                                Vector2 right = Vector2.Perpendicular(forward);
                                float arrowSize = 1.5f / zoom;
                                Vector2 arrowOffset = -forward * 2f / zoom;
                                Vector2 corner1 = prev_point + (right * arrowSize) + (-forward * arrowSize);
                                Vector2 corner2 = prev_point + (-right * arrowSize) + (-forward * arrowSize);
                                DrawAAPolyLineNonAlloc(thickness, corner1 + arrowOffset, lerp + arrowOffset);
                                DrawAAPolyLineNonAlloc(thickness, corner2 + arrowOffset, lerp + arrowOffset);
                            }
                            prev_point = lerp;
                            if (stroke == NoodleStroke.Dashed && draw >= 2) draw = -2;
                        }
                    }
                    break;
                case NoodlePath.Angled:
                    for (int i = 0; i < length - 1; i++) {
                        if (i == length - 1) continue; // Skip last index
                        if (gridPoints[i].x <= gridPoints[i + 1].x - (50 / zoom)) {
                            float midpoint = (gridPoints[i].x + gridPoints[i + 1].x) * 0.5f;
                            Vector2 start_1 = gridPoints[i];
                            Vector2 end_1 = gridPoints[i + 1];
                            start_1.x = midpoint;
                            end_1.x = midpoint;
                            if (i == length - 2) {
                                DrawAAPolyLineNonAlloc(thickness, gridPoints[i], start_1);
                                Handles.color = gradient.Evaluate(0.5f);
                                DrawAAPolyLineNonAlloc(thickness, start_1, end_1);
                                Handles.color = gradient.Evaluate(1f);
                                DrawAAPolyLineNonAlloc(thickness, end_1, gridPoints[i + 1]);
                            } else {
                                DrawAAPolyLineNonAlloc(thickness, gridPoints[i], start_1);
                                DrawAAPolyLineNonAlloc(thickness, start_1, end_1);
                                DrawAAPolyLineNonAlloc(thickness, end_1, gridPoints[i + 1]);
                            }
                        } else {
                            float midpoint = (gridPoints[i].y + gridPoints[i + 1].y) * 0.5f;
                            Vector2 start_1 = gridPoints[i];
                            Vector2 end_1 = gridPoints[i + 1];
                            start_1.x += 25 / zoom;
                            end_1.x -= 25 / zoom;
                            Vector2 start_2 = start_1;
                            Vector2 end_2 = end_1;
                            start_2.y = midpoint;
                            end_2.y = midpoint;
                            if (i == length - 2) {
                                DrawAAPolyLineNonAlloc(thickness, gridPoints[i], start_1);
                                Handles.color = gradient.Evaluate(0.25f);
                                DrawAAPolyLineNonAlloc(thickness, start_1, start_2);
                                Handles.color = gradient.Evaluate(0.5f);
                                DrawAAPolyLineNonAlloc(thickness, start_2, end_2);
                                Handles.color = gradient.Evaluate(0.75f);
                                DrawAAPolyLineNonAlloc(thickness, end_2, end_1);
                                Handles.color = gradient.Evaluate(1f);
                                DrawAAPolyLineNonAlloc(thickness, end_1, gridPoints[i + 1]);
                            } else {
                                DrawAAPolyLineNonAlloc(thickness, gridPoints[i], start_1);
                                DrawAAPolyLineNonAlloc(thickness, start_1, start_2);
                                DrawAAPolyLineNonAlloc(thickness, start_2, end_2);
                                DrawAAPolyLineNonAlloc(thickness, end_2, end_1);
                                DrawAAPolyLineNonAlloc(thickness, end_1, gridPoints[i + 1]);
                            }
                        }
                    }
                    break;
                case NoodlePath.ShaderLab:
                    Vector2 start = gridPoints[0];
                    Vector2 end = gridPoints[length - 1];
                    //Modify first and last point in array so we can loop trough them nicely.
                    gridPoints[0] = gridPoints[0] + Vector2.right * (20 / zoom);
                    gridPoints[length - 1] = gridPoints[length - 1] + Vector2.left * (20 / zoom);
                    //Draw first vertical lines going out from nodes
                    Handles.color = gradient.Evaluate(0f);
                    DrawAAPolyLineNonAlloc(thickness, start, gridPoints[0]);
                    Handles.color = gradient.Evaluate(1f);
                    DrawAAPolyLineNonAlloc(thickness, end, gridPoints[length - 1]);
                    for (int i = 0; i < length - 1; i++) {
                        Vector2 point_a = gridPoints[i];
                        Vector2 point_b = gridPoints[i + 1];
                        // Draws the line with the coloring.
                        Vector2 prev_point = point_a;
                        // Approximately one segment per 5 pixels
                        int segments = (int) Vector2.Distance(point_a, point_b) / 5;
                        segments = Math.Max(segments, 1);

                        int draw = 0;
                        for (int j = 0; j <= segments; j++) {
                            draw++;
                            float t = j / (float) segments;
                            Vector2 lerp = Vector2.Lerp(point_a, point_b, t);
                            if (draw > 0) {
                                if (i == length - 2) Handles.color = gradient.Evaluate(t);
                                DrawAAPolyLineNonAlloc(thickness, prev_point, lerp);
                            }
                            prev_point = lerp;
                            if (stroke == NoodleStroke.Dashed && draw >= 2) draw = -2;
                        }
                    }
                    gridPoints[0] = start;
                    gridPoints[length - 1] = end;
                    break;
            }
            Handles.color = originalHandlesColor;
        }

        /// <summary> Draws all connections </summary>
        public void DrawConnections() {
            Vector2 mousePos = Event.current.mousePosition;
            List<RerouteReference> selection = preBoxSelectionReroute != null ? new List<RerouteReference>(preBoxSelectionReroute) : new List<RerouteReference>();
            hoveredReroute = new RerouteReference();

            List<Vector2> gridPoints = new List<Vector2>(2);

            Color col = GUI.color;
            foreach (XNode.Node node in graph.nodes) {
                //If a null node is found, return. This can happen if the nodes associated script is deleted. It is currently not possible in Unity to delete a null asset.
                if (node == null) continue;

                // Draw full connections and output > reroute
                foreach (XNode.NodePort output in node.Outputs) {
                    //Needs cleanup. Null checks are ugly
                    Rect fromRect;
                    if (!_portConnectionPoints.TryGetValue(output, out fromRect)) continue;

                    Color portColor = graphEditor.GetPortColor(output);
                    GUIStyle portStyle = graphEditor.GetPortStyle(output);

                    for (int k = 0; k < output.ConnectionCount; k++) {
                        XNode.NodePort input = output.GetConnection(k);

                        Gradient noodleGradient = graphEditor.GetNoodleGradient(output, input);
                        float noodleThickness = graphEditor.GetNoodleThickness(output, input);
                        NoodlePath noodlePath = graphEditor.GetNoodlePath(output, input);
                        NoodleStroke noodleStroke = graphEditor.GetNoodleStroke(output, input);

                        // Error handling
                        if (input == null) continue; //If a script has been updated and the port doesn't exist, it is removed and null is returned. If this happens, return.
                        if (!input.IsConnectedTo(output)) input.Connect(output);
                        Rect toRect;
                        if (!_portConnectionPoints.TryGetValue(input, out toRect)) continue;

                        List<Vector2> reroutePoints = output.GetReroutePoints(k);

                        gridPoints.Clear();
                        gridPoints.Add(fromRect.center);
                        gridPoints.AddRange(reroutePoints);
                        gridPoints.Add(toRect.center);
                        DrawNoodle(noodleGradient, noodlePath, noodleStroke, noodleThickness, gridPoints);

                        // Loop through reroute points again and draw the points
                        for (int i = 0; i < reroutePoints.Count; i++) {
                            RerouteReference rerouteRef = new RerouteReference(output, k, i);
                            // Draw reroute point at position
                            Rect rect = new Rect(reroutePoints[i], new Vector2(12, 12));
                            rect.position = new Vector2(rect.position.x - 6, rect.position.y - 6);
                            rect = GridToWindowRect(rect);

                            // Draw selected reroute points with an outline
                            if (selectedReroutes.Contains(rerouteRef)) {
                                GUI.color = NodeEditorPreferences.GetSettings().highlightColor;
                                GUI.DrawTexture(rect, portStyle.normal.background);
                            }

                            GUI.color = portColor;
                            if (noodlePath != NoodlePath.Arrows && noodlePath != NoodlePath.CurvyArrows)
                                GUI.DrawTexture(rect, portStyle.active.background);
                            if (rect.Overlaps(selectionBox)) selection.Add(rerouteRef);
                            if (rect.Contains(mousePos)) hoveredReroute = rerouteRef;

                        }
                    }
                }
            }
            GUI.color = col;
            if (Event.current.type != EventType.Layout && currentActivity == NodeActivity.DragGrid) selectedReroutes = selection;
        }

        private void DrawNodes() {
            Event e = Event.current;
            if (e.type == EventType.Layout) {
                selectionCache = new List<UnityEngine.Object>(Selection.objects);
            }

            System.Reflection.MethodInfo onValidate = null;
            if (Selection.activeObject != null && Selection.activeObject is XNode.Node) {
                onValidate = Selection.activeObject.GetType().GetMethod("OnValidate");
                if (onValidate != null) EditorGUI.BeginChangeCheck();
            }

            BeginZoomed(position, zoom, topPadding);

            Vector2 mousePos = Event.current.mousePosition;

            if (e.type != EventType.Layout) {
                hoveredNode = null;
                hoveredPort = null;
            }

            List<UnityEngine.Object> preSelection = preBoxSelection != null ? new List<UnityEngine.Object>(preBoxSelection) : new List<UnityEngine.Object>();

            // Selection box stuff
            Vector2 boxStartPos = GridToWindowPositionNoClipped(dragBoxStart);
            Vector2 boxSize = mousePos - boxStartPos;
            if (boxSize.x < 0) { boxStartPos.x += boxSize.x; boxSize.x = Mathf.Abs(boxSize.x); }
            if (boxSize.y < 0) { boxStartPos.y += boxSize.y; boxSize.y = Mathf.Abs(boxSize.y); }
            Rect selectionBox = new Rect(boxStartPos, boxSize);

            //Save guiColor so we can revert it
            Color guiColor = GUI.color;

            List<XNode.NodePort> removeEntries = new List<XNode.NodePort>();
            selectedNodes.Clear();

            if (e.type == EventType.Layout) culledNodes = new List<XNode.Node>();

            //First get selected nodes
            for (int n = 0; n < graph.nodes.Count; n++)
            {
                XNode.Node node = graph.nodes[n];
                bool selected = selectionCache.Contains(graph.nodes[n]);

                if (selected)
                    selectedNodes.Add(graph.nodes[n]);
            }

            //Populate linked nodes and draw arrow to them
            List<Node> linkedNodes = new List<Node>();
            if (selectedNodes.Count == 1)
            {
                foreach (var selectedNode in selectedNodes)
                {
                    if (!selectedNode.LinkedNodes.IsNullOrEmpty())
                    {
                        Color originalHandlesColor = Handles.color;
                        linkedNodes = linkedNodes.Union(selectedNode.LinkedNodes).ToList();
                        linkedNodes.ForEach(linkedNode =>
                        {
                            Vector2 offsetVec = Vector2.right * 100 + Vector2.up * 50;
                            Vector2 startPoint = selectedNode.position + offsetVec;
                            Vector2 endPoint = linkedNode.position + offsetVec;

                            Vector2 startToEnd = endPoint - startPoint;
                            float distance = startToEnd.magnitude;
                            int segments = Mathf.CeilToInt(distance / 10f);

                            for (int i = 0; i < segments; i++)
                            {
                                Handles.color = Color.blue;
                                if (i % 4 == 0) continue;
                                // Vector2 start = GridToWindowPositionNoClipped(startPoint + i * (distance / segments) * startToEnd);
                                // Vector2 end = GridToWindowPositionNoClipped(startPoint + (i + 1f) * (distance / segments) * startToEnd);
                                Vector2 start = GridToWindowPositionNoClipped(Vector2.Lerp(startPoint, endPoint, (float) i/segments));
                                Vector2 end = GridToWindowPositionNoClipped(Vector2.Lerp(startPoint, endPoint, (float) (i+1)/segments));
                                DrawAAPolyLineNonAlloc(20f, start, end);
                            }

                            for (int i = 0; i < segments; i++)
                            {
                                Handles.color = Color.cyan;
                                if (i % 4 == 0) continue;
                                // Vector2 start = GridToWindowPositionNoClipped(startPoint + i * (distance / segments) * startToEnd);
                                // Vector2 end = GridToWindowPositionNoClipped(startPoint + (i + 1f) * (distance / segments) * startToEnd);
                                Vector2 start = GridToWindowPositionNoClipped(Vector2.Lerp(startPoint, endPoint, (float) i/segments));
                                Vector2 end = GridToWindowPositionNoClipped(Vector2.Lerp(startPoint, endPoint, (float) (i+1)/segments));
                                DrawAAPolyLineNonAlloc(10f, start, end);
                            }


                            //Draw arrows at ends
                            // Vector2 towardLinked = endPoint - startPoint;
                            // Vector2 right = Vector2.Perpendicular(towardLinked);
                            // float arrowLength = .1f / zoom;
                            // float arrowWidth = .1f / zoom;
                            // Vector2 corner1 = endPoint - towardLinked * arrowLength + right * arrowWidth;
                            // Vector2 corner2 = endPoint - towardLinked * arrowLength - right * arrowWidth;
                            // DrawAAPolyLineNonAlloc(20f, corner1, endPoint);
                            // DrawAAPolyLineNonAlloc(20f, corner2, endPoint);
                        });
                        Handles.color = originalHandlesColor;
                    }
                }
            }

            for (int n = 0; n < graph.nodes.Count; n++) {
                // Skip null nodes. The user could be in the process of renaming scripts, so removing them at this point is not advisable.
                if (graph.nodes[n] == null) continue;
                if (n >= graph.nodes.Count) return;
                XNode.Node node = graph.nodes[n];

                // Culling
                if (e.type == EventType.Layout) {
                    // Cull unselected nodes outside view
                    if (!Selection.Contains(node) && ShouldBeCulled(node)) {
                        culledNodes.Add(node);
                        continue;
                    }
                } else if (culledNodes.Contains(node)) continue;

                if (e.type == EventType.Repaint) {
                    removeEntries.Clear();
                    foreach (var kvp in _portConnectionPoints)
                        if (kvp.Key.node == node) removeEntries.Add(kvp.Key);
                    foreach (var k in removeEntries) _portConnectionPoints.Remove(k);
                }

                NodeEditor nodeEditor = NodeEditor.GetEditor(node, this);

                NodeEditor.portPositions.Clear();

                // Set default label width. This is potentially overridden in OnBodyGUI
                EditorGUIUtility.labelWidth = 84;

                //Get node position
                Vector2 nodePos = GridToWindowPositionNoClipped(node.position);

                GUILayout.BeginArea(new Rect(nodePos, new Vector2(nodeEditor.GetWidth(), 4000)));

                bool selected = selectionCache.Contains(graph.nodes[n]);
                bool isLinkedNode = linkedNodes.Contains(node);

                if (selected) {
                    GUIStyle style = new GUIStyle(nodeEditor.GetBodyStyle());
                    GUIStyle highlightStyle = new GUIStyle(nodeEditor.GetBodyHighlightStyle());
                    highlightStyle.padding = style.padding;
                    style.padding = new RectOffset();
                    GUI.color = nodeEditor.GetTint();
                    GUILayout.BeginVertical(style);
                    GUI.color = NodeEditorPreferences.GetSettings().highlightColor;
                    GUILayout.BeginVertical(new GUIStyle(highlightStyle));
                }
                else if (isLinkedNode)
                {
                    GUIStyle style = new GUIStyle(nodeEditor.GetBodyStyle());
                    GUIStyle highlightStyle = new GUIStyle(nodeEditor.GetBodyHighlightStyle());
                    highlightStyle.padding = style.padding;
                    style.padding = new RectOffset();
                    GUI.color = nodeEditor.GetTint();
                    GUILayout.BeginVertical(style);
                    GUI.color = new Color(20f/255f, 200f/255f, 200f/255f);
                    GUILayout.BeginVertical(new GUIStyle(highlightStyle));
                }
                else {
                    GUIStyle style = new GUIStyle(nodeEditor.GetBodyStyle());
                    GUI.color = nodeEditor.GetTint();
                    GUILayout.BeginVertical(style);
                }

                GUI.color = guiColor;
                EditorGUI.BeginChangeCheck();

                //Draw node contents
                nodeEditor.OnHeaderGUI();
                nodeEditor.OnBodyGUI();

                //If user changed a value, notify other scripts through onUpdateNode
                if (EditorGUI.EndChangeCheck()) {
                    if (NodeEditor.onUpdateNode != null) NodeEditor.onUpdateNode(node);
                    EditorUtility.SetDirty(node);
                    nodeEditor.serializedObject.ApplyModifiedProperties();
                }

                GUILayout.EndVertical();

                //Cache data about the node for next frame
                if (e.type == EventType.Repaint) {
                    Vector2 size = GUILayoutUtility.GetLastRect().size;
                    if (nodeSizes.ContainsKey(node)) nodeSizes[node] = size;
                    else nodeSizes.Add(node, size);

                    foreach (var kvp in NodeEditor.portPositions) {
                        Vector2 portHandlePos = kvp.Value;
                        portHandlePos += node.position;
                        Rect rect = new Rect(portHandlePos.x - 8, portHandlePos.y - 8, 16, 16);
                        portConnectionPoints[kvp.Key] = rect;
                    }
                }

                if (selected || isLinkedNode) GUILayout.EndVertical();

                if (e.type != EventType.Layout) {
                    //Check if we are hovering this node
                    Vector2 nodeSize = GUILayoutUtility.GetLastRect().size;
                    Rect windowRect = new Rect(nodePos, nodeSize);
                    if (windowRect.Contains(mousePos)) hoveredNode = node;

                    //If dragging a selection box, add nodes inside to selection
                    //Dont allow drag select of comment nodes
                    if (currentActivity == NodeActivity.DragGrid &&
                        !(node is CommentNode)) {
                        if (windowRect.Overlaps(selectionBox)) preSelection.Add(node);
                    }

                    //Check if we are hovering any of this nodes ports
                    //Check input ports
                    foreach (XNode.NodePort input in node.Inputs) {
                        //Check if port rect is available
                        if (!portConnectionPoints.ContainsKey(input)) continue;
                        Rect r = GridToWindowRectNoClipped(portConnectionPoints[input]);
                        if (r.Contains(mousePos)) hoveredPort = input;
                    }
                    //Check all output ports
                    foreach (XNode.NodePort output in node.Outputs) {
                        //Check if port rect is available
                        if (!portConnectionPoints.ContainsKey(output)) continue;
                        Rect r = GridToWindowRectNoClipped(portConnectionPoints[output]);
                        if (r.Contains(mousePos)) hoveredPort = output;
                    }
                }

                GUILayout.EndArea();
            }

            if (e.type != EventType.Layout && currentActivity == NodeActivity.DragGrid) Selection.objects = preSelection.ToArray();
            EndZoomed(position, zoom, topPadding);

            //If a change in is detected in the selected node, call OnValidate method.
            //This is done through reflection because OnValidate is only relevant in editor,
            //and thus, the code should not be included in build.
            if (onValidate != null && EditorGUI.EndChangeCheck()) onValidate.Invoke(Selection.activeObject, null);
        }

        private bool ShouldBeCulled(XNode.Node node) {

            Vector2 nodePos = GridToWindowPositionNoClipped(node.position);
            if (nodePos.x / _zoom > position.width) return true; // Right
            else if (nodePos.y / _zoom > position.height) return true; // Bottom
            else if (nodeSizes.ContainsKey(node)) {
                Vector2 size = nodeSizes[node];
                if (nodePos.x + size.x < 0) return true; // Left
                else if (nodePos.y + size.y < 0) return true; // Top
            }
            return false;
        }

        private void DrawTooltip() {
            if (!NodeEditorPreferences.GetSettings().portTooltips || graphEditor == null)
                return;
            string tooltip = null;
            if (hoveredPort != null) {
                tooltip = graphEditor.GetPortTooltip(hoveredPort);
            } else if (hoveredNode != null && IsHoveringNode && IsHoveringTitle(hoveredNode)) {
                tooltip = NodeEditor.GetEditor(hoveredNode, this).GetHeaderTooltip();
            }
            if (string.IsNullOrEmpty(tooltip)) return;
            GUIContent content = new GUIContent(tooltip);
            Vector2 size = NodeEditorResources.styles.tooltip.CalcSize(content);
            size.x += 8;
            Rect rect = new Rect(Event.current.mousePosition - (size), size);
            EditorGUI.LabelField(rect, content, NodeEditorResources.styles.tooltip);
            Repaint();
        }

        private void DrawMiniMap()
        {
            #region MinimapBackground

            Rect windowRect = NodeEditorWindow.current.position;

            (float top, float right, float bottom, float left) margins = (0f, 0f, 15f, 15f);
            float aspectRatio = miniMapRect.size.x / miniMapRect.size.y;

            float x = margins.left;
            float y = windowRect.height - margins.bottom - miniMapRect.size.y;

            Vector2 miniMapPos = new Vector2(x, y);
            Vector2 miniMapDim = NodeEditorPreferences.GetSettings().minimapSize;
            miniMapCenter = miniMapPos + miniMapDim / 2;
            miniMapRect = new Rect(miniMapPos, miniMapDim);
            Color color = new Color(.1f, .1f ,.1f, 1f);
            EditorGUI.DrawRect(miniMapRect, color);
            XNodeUtils.DrawBorderAroundRect(miniMapRect, 3f, new Color(.6f, .6f, .6f, 1f));

            #endregion

            #region Drag Rect

            Color dragRectColor = new Color(.6f, .6f, .6f, .35f);
            Rect dragRect = GetDragRect(miniMapRect);
            EditorGUI.DrawRect(dragRect, dragRectColor);
            EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.ResizeUpRight);

            #endregion

            #region Bounds

            float buttonIncrementFactor = 2f;


            //Buttons to control extents
            Vector2 buttonDim = new Vector2(miniMapRect.size.x / 10f, 20f);
            Vector2 buttonPos1 = miniMapPos + new Vector2(0f, -buttonDim.y - 5f);
            Vector2 buttonPos2 = miniMapPos + new Vector2(buttonDim.x, -buttonDim.y - 5f);
            Rect buttonRect1 = new Rect(buttonPos1, buttonDim);
            Rect buttonRect2 = new Rect(buttonPos2, buttonDim);

            if (GUI.Button(buttonRect1, "-"))
            {
                ZoomMinimap(buttonIncrementFactor);
            }

            if (GUI.Button(buttonRect2, "+"))
            {
                ZoomMinimap(1f/buttonIncrementFactor);
            }

            NodeEditorPreferences.Settings prefs = NodeEditorPreferences.GetSettings();
            //Make sure aspect of bounds matches aspect of minimap size
            Vector2 bounds = new Vector2(miniMapDim.x * prefs.miniMapZoomCurrentFactor, miniMapDim.y * prefs.miniMapZoomCurrentFactor);

            //Multiply node pos by this to get the position on minimap
            boundsToMinimapFactor.x = miniMapRect.size.x / bounds.x;
            boundsToMinimapFactor.y = miniMapRect.size.y / bounds.y;

            float halfWidthBounds = bounds.x / 2f;
            float halfHeightBounds = bounds.y / 2f;
            Vector2 topLeft = new Vector2(-halfWidthBounds, -halfHeightBounds);
            topLeft -= panOffset;

            Rect boundsRect = new Rect(topLeft, bounds);

            //Show minimap target on grid
            //EditorGUI.DrawRect(NodeEditorWindow.current.GridToWindowRect(boundsRect), new Color(.5f, .5f ,1f, .3f));

            #endregion

            #region Map Look Bounds To Minimap

            //Show the current view bounds on the minimap
            Rect viewRect = new Rect();
            viewRect.size = windowRect.size * zoom;
            viewRect.position = -panOffset - viewRect.size / 2f;

            //EditorGUI.DrawRect(NodeEditorWindow.current.GridToWindowRect(viewRect), new Color(1f, 1f, .2f, .1f));

            Vector2 viewMinimapSize = WindowToMinimapPos(viewRect.size);

            Vector2 viewPosMinimap = miniMapCenter - viewMinimapSize/2f;
            //viewPosMinimap.x = miniMapCenter.x + viewRect.position.x * boundsToMinimapX;
            //viewPosMinimap.y = miniMapCenter.y + viewRect.position.y * boundsToMinimapY;
            Rect viewMinimapRect = new Rect(viewPosMinimap, viewMinimapSize);
            viewMinimapRect = XNodeUtils.ClampToRect(viewMinimapRect, miniMapRect);

            EditorGUI.DrawRect(viewMinimapRect, new Color(1f, 1f, .2f, .02f));
            XNodeUtils.DrawBorderAroundRect(viewMinimapRect, 3f, new Color(1f, 1f, .2f, .35f));

            #endregion

            #region MinimapCenterLines

            Vector2 originOnMinimap =
                miniMapCenter + WindowToMinimapPos(panOffset);

            float centerLineThickness = 1.5f;

            Vector2 verticalCenterLinePos = new Vector2();
            verticalCenterLinePos.x = originOnMinimap.x;
            verticalCenterLinePos.y = originOnMinimap.y - (originOnMinimap.y - miniMapRect.position.y);

            Vector2 verticalCenterLineEnd = verticalCenterLinePos + new Vector2(0f, miniMapDim.y);

            Vector2 horizontalCenterLinePos = new Vector2();
            horizontalCenterLinePos.x = originOnMinimap.x - (originOnMinimap.x - miniMapRect.position.x);
            horizontalCenterLinePos.y = originOnMinimap.y;

            Vector2 horizontalCenterLineEnd = horizontalCenterLinePos + new Vector2(miniMapDim.x, 0f);

            Color prevColor = Handles.color;
            Handles.color = new Color(.6f, .6f, .6f, .65f);

            //Dont draw if not on minimap
            if (miniMapRect.Contains(new Vector2(originOnMinimap.x, miniMapCenter.y)))
                Handles.DrawAAPolyLine(centerLineThickness, new Vector3[] {verticalCenterLinePos, verticalCenterLineEnd});

            if (miniMapRect.Contains(new Vector2(miniMapCenter.x, originOnMinimap.y)))
                Handles.DrawAAPolyLine(centerLineThickness, new Vector3[] {horizontalCenterLinePos, horizontalCenterLineEnd});

            Handles.color = prevColor;

            #endregion

        }

        private void DrawNodesMinimap()
        {
            var nodes = NodeEditorWindow.current.graph.nodes.Union(culledNodes);
            foreach (var node in nodes)
            {
                var nodePos = node.position;
                Rect nodeRect = new Rect(new Vector2(nodePos.x, nodePos.y), new Vector2(100f, 100f));
                Rect nodeWindowRect = NodeEditorWindow.current.GridToWindowRect(nodeRect);

                //If cant find size of node, use temp value
                //Do this to prevent cold start and empty minimap since nodeSize is cached
                Vector2 nodeSize;
                if (!nodeSizes.ContainsKey(node))
                    nodeSize = new Vector2(500f, 200f);
                else
                    nodeSize = nodeSizes[node];

                //EditorGUI.DrawRect(nodeWindowRect, Color.green);
                Vector2 minimapPanOffset = WindowToMinimapPos(panOffset);
                Vector2 nodePosMiniMap = miniMapCenter + minimapPanOffset + WindowToMinimapPos(nodePos);

                nodeSize = WindowToMinimapPos(nodeSize);

                Rect nodeMinimapRect = new Rect(nodePosMiniMap, nodeSize);
                NodeEditor nodeEditor = NodeEditor.GetEditor(node, this);
                Color nodeColor = nodeEditor.GetTint();

                //Make all nodes brighter by finding factor that pushes max value to 1f
                float maxRGB = Mathf.Max(nodeColor.r, nodeColor.g, nodeColor.b);
                float brightnessFactor = 1f / maxRGB;
                nodeColor = nodeColor * brightnessFactor;

                //Dont draw if falls outside the minimap (both ends must be within)
                Vector2 rightCorner = nodeMinimapRect.position + Vector2.right * nodeMinimapRect.size.x;
                if (miniMapRect.Contains(nodeMinimapRect.position) &&
                    miniMapRect.Contains(rightCorner))
                {
                    EditorGUI.DrawRect(nodeMinimapRect, nodeColor);

                    //Draw description of comment node at higher zoom levels
                    if (node is CommentNode)
                    {
                        float cullingExtentFactor = 60f;

                        var nodeAsComment = node as CommentNode;
                        NodeEditorPreferences.Settings prefs = NodeEditorPreferences.GetSettings();
                        GUIStyle style = XNodeUtils.ZoomBasedStyle(3f, 12f,
                            prefs.miniMapZoomCurrentFactor,  cullingExtentFactor, minimapZoomMinFactor,
                            FontStyle.Normal, TextAnchor.LowerCenter);
                        style.clipping = TextClipping.Overflow;

                        float verticalOffset = WindowToMinimapPos(new Vector2(0f, 800f)).y;
                        Vector2 commentRectPos = new Vector2(nodePosMiniMap.x, nodePosMiniMap.y - verticalOffset);
                        Vector2 commentRectSize = new Vector2(nodeSize.x, verticalOffset);
                        Rect commentRect = new Rect(commentRectPos, commentRectSize);

                        if (prefs.miniMapZoomCurrentFactor < cullingExtentFactor)
                            EditorGUI.LabelField(commentRect, nodeAsComment.Description, style);

                        nodeColor.a = .2f;
                        //EditorGUI.DrawRect(commentRect, new Color(.1f, 1f, 1f, .25f));
                        XNodeUtils.DrawBorderAroundRect(commentRect, 1f, nodeColor);
                    }
                }

            }
        }

        private void HandleMinimapEvents()
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    OnMouseDown(e);
                    RepaintAll();
                    break;
                case EventType.MouseDrag:
                    OnMouseDrag(e);
                    RepaintAll();
                    break;
                case EventType.MouseUp:
                    OnMouseUp(e);
                    RepaintAll();
                    break;
                case EventType.ScrollWheel:
                    OnScrollWheel(e);
                    RepaintAll();
                    break;
            }
        }

        private void OnMouseDown(Event e)
        {
            if (!miniMapRect.Contains(e.mousePosition))
                return;

            mouseDownPos = e.mousePosition;

            //Check for resize
            if (GetDragRect(miniMapRect).Contains(e.mousePosition))
            {
                isResizingMinimap = true;
                return;
            }

            isDraggingMinimap = true;
        }

        private void OnMouseDrag(Event e)
        {
            //Handle Resize
            if (isResizingMinimap)
            {
                Vector2 newSize = NodeEditorPreferences.GetSettings().minimapSize;
                newSize.x = Mathf.Max(minMinimapSize, newSize.x + (int)e.delta.x);
                newSize.y = Mathf.Max(minMinimapSize, newSize.y - (int)e.delta.y);
                NodeEditorPreferences.GetSettings().minimapSize = newSize;

                return;
            }

            if (!isDraggingMinimap)
                return;

            Vector2 mapToScreenDelta = MimimapToWindowPos(e.delta);
            panOffset += mapToScreenDelta;
        }

        private void OnMouseUp(Event e)
        {
            //Store previous pan location
            if (panOffset != prevPanOffset)
            {
                prevPanOffset = panOffset;
            }

            //Pan to selected pos
            if (mouseDownPos == e.mousePosition)
            {
                Vector2 centerMiniMapToMouse = MimimapToWindowPos(e.mousePosition - miniMapCenter);
                panOffset -= centerMiniMapToMouse;
            }

            isDraggingMinimap = false;
            isResizingMinimap = false;
        }

        private void OnScrollWheel(Event e)
        {
            if (!miniMapRect.Contains(e.mousePosition))
                return;

            float zoomFactor = 1.5f;
            if (e.delta.y > 0f)
            {
                ZoomMinimap(zoomFactor);
            }
            else
            {
                ZoomMinimap(1f/zoomFactor);
            }
        }

        private Rect GetDragRect(Rect containerRect)
        {
            int cornerDragSize = 15;
            var dragRect = containerRect;
            dragRect.x += dragRect.width - cornerDragSize;
            dragRect.width = cornerDragSize;
            dragRect.height = cornerDragSize;

            return dragRect;
        }

        private void ZoomMinimap(float zoomFactor)
        {
            NodeEditorPreferences.Settings prefs = NodeEditorPreferences.GetSettings();

            prefs.miniMapZoomCurrentFactor *= zoomFactor;
            prefs.miniMapZoomCurrentFactor = Mathf.Max(minimapZoomMinFactor, prefs.miniMapZoomCurrentFactor);
            prefs.miniMapZoomCurrentFactor = Mathf.Min(minimapZoomMaxFactor, prefs.miniMapZoomCurrentFactor);

        }

        private Vector2 WindowToMinimapPos(Vector2 windowPos)
        {
            return new Vector2(windowPos.x * boundsToMinimapFactor.x, windowPos.y * boundsToMinimapFactor.y);
        }

        private Vector2 MimimapToWindowPos(Vector2 minimapPos)
        {
            return new Vector2(minimapPos.x / boundsToMinimapFactor.x, minimapPos.y / boundsToMinimapFactor.y);
        }
    }
}
