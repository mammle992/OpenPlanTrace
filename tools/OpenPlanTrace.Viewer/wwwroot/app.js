import * as pdfjsLib from "https://cdn.jsdelivr.net/npm/pdfjs-dist@4.10.38/build/pdf.mjs";

pdfjsLib.GlobalWorkerOptions.workerSrc = "https://cdn.jsdelivr.net/npm/pdfjs-dist@4.10.38/build/pdf.worker.mjs";

const defaultBenchmarkFilters = {
  query: "",
  detector: "all",
  queueKind: "all",
  status: "all",
  issue: "all",
  page: "all"
};

const defaultBenchmarkManualTargetDraft = {
  detectorKey: "wallMetrics",
  id: "",
  label: "",
  text: "",
  marker: "",
  kind: "",
  subtype: "",
  routingSourceKind: "",
  routingObstacleKind: "",
  routingInfluence: "",
  structuralInfluence: "",
  roomUseKind: "",
  suppressesChildObjects: "",
  objectCandidateId: "",
  suppressedByAggregateId: "",
  suppressionReason: "",
  suppressionAction: "",
  replacementRoutingObstacleId: "",
  roomUseHintId: "",
  minCount: "",
  requiresReview: "",
  pageNumber: "",
  bounds: null,
  drawing: false
};

const defaultKvemoFilters = {
  query: "",
  priority: "all",
  trainingUse: "all",
  sourceKind: "all",
  sourceWallComponentKind: "all"
};

const defaultObjectReviewCropPadding = 18;
const currentKvemoCropSchemaVersion = "openplantrace.kvemo-crops.v2";
const supportedKvemoCropSchemaVersions = new Set([
  currentKvemoCropSchemaVersion,
  "openplantrace.kvemo-crops.v1"
]);
const currentObjectReviewDatasetSchemaVersion = "openplantrace.object-review-dataset.v2";
const viewerCleanWallMinSpanLength = 8.0;
const viewerCleanWallMergeGap = 12.0;
const viewerOrthogonalSkewRatio = 0.04;
const viewerOrthogonalSkewDrawingUnits = 8.0;
const viewerOffAxisConnectorTolerance = 1.25;

const defaultEnabledLayers = [
  "regions",
  "dimensions",
  "gridAxes",
  "annotations",
  "wallBodyFootprints",
  "wallTopologySpans",
  "rooms",
  "openings",
  "objectAggregates",
  "surfacePatterns",
  "placementIssues",
  "routingLayer"
];

const wallQaEnabledLayers = [
  "wallTopologySpans"
];

const overlayLegendItems = [
  { key: "regions", label: "Regions", stroke: "#147c72", fill: "rgba(20, 124, 114, 0.07)" },
  { key: "dimensions", label: "Dimensions", stroke: "#7854a8", fill: "rgba(120, 84, 168, 0.045)", dash: "5 4" },
  { key: "gridAxes", label: "Grid axes", stroke: "#6b7c1f", fill: "rgba(107, 124, 31, 0.06)", dash: "7 5" },
  { key: "gridBays", label: "Grid bays", stroke: "#6b7c1f", fill: "rgba(107, 124, 31, 0.08)", dash: "3 4" },
  { key: "annotations", label: "Annotations", stroke: "#2587b4", fill: "rgba(37, 135, 180, 0.055)" },
  { key: "wallComponents", label: "Wall components", stroke: "#c97c18", fill: "rgba(201, 124, 24, 0.06)", dash: "6 4" },
  { key: "walls", label: "Placement walls", stroke: "#0f4fb8", fill: "rgba(15, 79, 184, 0.06)" },
  { key: "wallBodyFootprints", label: "Wall body footprints", stroke: "#0f4fb8", fill: "rgba(15, 79, 184, 0.10)" },
  { key: "wallTopologySpans", label: "Clean wall spans", stroke: "#0f4fb8", fill: "rgba(15, 79, 184, 0.06)" },
  { key: "wallTopologyReviewSpans", label: "Non-placement wall spans", stroke: "#a65f00", fill: "rgba(166, 95, 0, 0.055)", dash: "3 3" },
  { key: "nodes", label: "Wall nodes", stroke: "#191a1f", fill: "#ffffff" },
  { key: "rooms", label: "Rooms", stroke: "#3f8f57", fill: "rgba(63, 143, 87, 0.08)" },
  { key: "roomClusters", label: "Room clusters", stroke: "#3f8f57", fill: "rgba(63, 143, 87, 0.05)", dash: "8 5" },
  { key: "roomAdjacency", label: "Room links", stroke: "#386fc3", fill: "rgba(56, 111, 195, 0.06)", dash: "4 4" },
  { key: "openings", label: "Openings", stroke: "#386fc3", fill: "rgba(56, 111, 195, 0.11)" },
  { key: "objectAggregates", label: "Object aggregates", stroke: "#8f5f12", fill: "rgba(143, 95, 18, 0.06)", dash: "6 3" },
  { key: "surfacePatterns", label: "Surface patterns", stroke: "#0f6b78", fill: "rgba(15, 107, 120, 0.055)", dash: "5 4" },
  { key: "placementIssues", label: "Placement issues", stroke: "#b82f42", fill: "rgba(196, 61, 61, 0.065)", dash: "2 3" },
  { key: "suppressedDetails", label: "Suppressed details", stroke: "#0f6b78", fill: "rgba(15, 107, 120, 0.045)", dash: "2 4" },
  { key: "wallGraphGaps", label: "Wall graph gaps", stroke: "#a65f00", fill: "rgba(166, 95, 0, 0.055)", dash: "3 3" },
  { key: "wallGraphRepairs", label: "Wall repairs", stroke: "#d04b24", fill: "rgba(208, 75, 36, 0.06)", dash: "2 2" },
  { key: "reviewQueue", label: "Scan review queue", stroke: "#c43d3d", fill: "rgba(196, 61, 61, 0.08)", dash: "7 4" },
  { key: "routingLayer", label: "Trusted routing layer", stroke: "#0f6b78", fill: "rgba(15, 107, 120, 0.07)", dash: "3 3" },
  { key: "objects", label: "Objects", stroke: "#7854a8", fill: "rgba(120, 84, 168, 0.10)" },
  { key: "benchmarkTargets", label: "Benchmark targets", stroke: "#1f559b", fill: "rgba(31, 85, 155, 0.10)", dash: "5 4" },
  { key: "compare", label: "Compare deltas", stroke: "#c43d3d", fill: "rgba(196, 61, 61, 0.08)" }
];

const placementOverlayLayerKeys = new Set([
  "walls",
  "wallBodyFootprints",
  "wallTopologySpans",
  "wallTopologyReviewSpans",
  "rooms",
  "openings",
  "objectAggregates",
  "surfacePatterns",
  "placementIssues",
  "wallGraphRepairs",
  "routingLayer"
]);

function initialEnabledLayerSet() {
  const layers = new Set(defaultEnabledLayers);
  const params = new URLSearchParams(window.location.search);
  const requested = params.get("layers") || params.get("layer");
  if (!requested) {
    return layers;
  }

  requested.split(",")
    .map((value) => value.trim())
    .filter(Boolean)
    .forEach((value) => {
      if (value === "all") {
        overlayLegendItems.forEach((item) => layers.add(item.key));
      } else if (value.startsWith("-")) {
        layers.delete(value.slice(1));
      } else {
        layers.add(value);
      }
    });

  return layers;
}

function initialWorkspaceTab() {
  const tab = new URLSearchParams(window.location.search).get("tab");
  return ["visualizer", "ai", "general", "pipeline", "advanced"].includes(tab) ? tab : "visualizer";
}

const state = {
  pdf: null,
  scan: null,
  visualSnapshot: null,
  placement: null,
  currentPage: 1,
  rendering: false,
  viewerOperationRevision: 0,
  documentLoadRevision: 0,
  benchmarkOverlayRevision: 0,
  sourceMode: "empty",
  enabledLayers: initialEnabledLayerSet(),
  enabledSourceLayers: new Set(),
  compare: null,
  benchmarkComparison: null,
  batchComparison: null,
  benchmarkResult: null,
  benchmarkManifest: null,
  benchmarkTargets: [],
  benchmarkReviewDecisions: new Map(),
  benchmarkTargetEdits: new Map(),
  benchmarkDeletedTargets: new Set(),
  benchmarkAddedTargetSequence: 1,
  pendingBenchmarkReviewSession: null,
  benchmarkFilters: { ...defaultBenchmarkFilters },
  benchmarkManualTargetDraft: resetBenchmarkManualTargetDraft(),
  benchmarkDrawBox: null,
  benchmarkSuppressNextOverlayClick: false,
  kvemo: null,
  kvemoFilters: { ...defaultKvemoFilters },
  activeWorkspaceTab: initialWorkspaceTab(),
  selectedItem: null
};

function beginDocumentLoad() {
  state.documentLoadRevision += 1;
  return state.documentLoadRevision;
}

function beginViewerOperation() {
  state.viewerOperationRevision += 1;
  return state.viewerOperationRevision;
}

function viewerOperationChangedSince(revision) {
  return revision !== state.viewerOperationRevision;
}

function beginBenchmarkOverlayLoad() {
  state.benchmarkOverlayRevision += 1;
  return state.benchmarkOverlayRevision;
}

function isCurrentDocumentLoad(revision) {
  return revision == null || revision === state.documentLoadRevision;
}

function benchmarkOverlayChangedSince(revision) {
  return revision !== state.benchmarkOverlayRevision;
}

const elements = {
  fileInput: document.querySelector("#pdfInput"),
  dropZone: document.querySelector("#dropZone"),
  fileMeta: document.querySelector("#fileMeta"),
  status: document.querySelector("#status"),
  pageLabel: document.querySelector("#pageLabel"),
  prevPage: document.querySelector("#prevPage"),
  nextPage: document.querySelector("#nextPage"),
  downloadJson: document.querySelector("#downloadJson"),
  downloadLayers: document.querySelector("#downloadLayers"),
  downloadCalibration: document.querySelector("#downloadCalibration"),
  downloadTitleBlocks: document.querySelector("#downloadTitleBlocks"),
  downloadDimensions: document.querySelector("#downloadDimensions"),
  downloadGridAxes: document.querySelector("#downloadGridAxes"),
  downloadGridBays: document.querySelector("#downloadGridBays"),
  downloadAnnotations: document.querySelector("#downloadAnnotations"),
  downloadObjectGroups: document.querySelector("#downloadObjectGroups"),
  downloadObjectLabelProfile: document.querySelector("#downloadObjectLabelProfile"),
  downloadObjectReviewDataset: document.querySelector("#downloadObjectReviewDataset"),
  downloadObjectCorrectionDataset: document.querySelector("#downloadObjectCorrectionDataset"),
  downloadBenchmark: document.querySelector("#downloadBenchmark"),
  downloadSvg: document.querySelector("#downloadSvg"),
  applyDefaultLayers: document.querySelector("#applyDefaultLayers"),
  applyWallQaLayers: document.querySelector("#applyWallQaLayers"),
  stage: document.querySelector("#stage"),
  emptyState: document.querySelector("#emptyState"),
  pageFrame: document.querySelector("#pageFrame"),
  sourceUnderlayBadge: document.querySelector("#sourceUnderlayBadge"),
  kvemoReview: document.querySelector("#kvemoReview"),
  canvas: document.querySelector("#pdfCanvas"),
  overlay: document.querySelector("#overlay"),
  counts: document.querySelector("#counts"),
  sourceLayerList: document.querySelector("#sourceLayerList"),
  calibrationDetails: document.querySelector("#calibrationDetails"),
  qualityDetails: document.querySelector("#qualityDetails"),
  titleBlockDetails: document.querySelector("#titleBlockDetails"),
  compareDetails: document.querySelector("#compareDetails"),
  benchmarkDetails: document.querySelector("#benchmarkDetails"),
  objectGroupList: document.querySelector("#objectGroupList"),
  selectionDetails: document.querySelector("#selectionDetails"),
  diagnosticsList: document.querySelector("#diagnosticsList"),
  coordinateDetails: document.querySelector("#coordinateDetails"),
  legendList: document.querySelector("#legendList"),
  cursorCoordinates: document.querySelector("#cursorCoordinates"),
  selectionCoordinates: document.querySelector("#selectionCoordinates"),
  analysisCounts: document.querySelector("#analysisCounts"),
  workspaceTabButtons: [...document.querySelectorAll("[data-workspace-tab]")],
  workspacePanels: {
    visualizer: document.querySelector("#tabVisualizer"),
    ai: document.querySelector("#tabAi"),
    general: document.querySelector("#tabGeneral"),
    pipeline: document.querySelector("#tabPipeline"),
    advanced: document.querySelector("#tabAdvanced")
  },
  aiTabDetails: document.querySelector("#aiTabDetails"),
  generalTabDetails: document.querySelector("#generalTabDetails"),
  pipelineTabDetails: document.querySelector("#pipelineTabDetails"),
  advancedTabDetails: document.querySelector("#advancedTabDetails")
};

const context = elements.canvas.getContext("2d");

const comparableLayers = [
  { key: "regions", label: "Regions", layer: "regions", geometry: "rect" },
  { key: "titleBlocks", label: "Title blocks" },
  { key: "dimensions", label: "Dimensions", layer: "dimensions", geometry: "dimension" },
  { key: "gridAxes", label: "Grid axes", layer: "gridAxes", geometry: "axis" },
  { key: "gridBaySpacings", label: "Grid bays", layer: "gridBays", geometry: "axis" },
  { key: "annotations", label: "Annotations", layer: "annotations", geometry: "rect" },
  { key: "surfacePatterns", label: "Surface patterns", layer: "surfacePatterns", geometry: "rect" },
  { key: "wallComponents", label: "Wall components", layer: "wallComponents", geometry: "rect" },
  { key: "walls", label: "Placement walls", layer: "walls", geometry: "wallLine" },
  { key: "nodes", label: "Nodes", layer: "nodes", geometry: "point" },
  { key: "rooms", label: "Rooms", layer: "rooms", geometry: "room" },
  { key: "roomAdjacencyEdges", label: "Room links" },
  { key: "roomClusters", label: "Room clusters", layer: "roomClusters", geometry: "rect" },
  { key: "openings", label: "Openings", layer: "openings", geometry: "rect" },
  { key: "objects", label: "Objects", layer: "objects", geometry: "rect" },
  { key: "objectAggregates", label: "Object aggregates", layer: "objectAggregates", geometry: "rect" },
  { key: "wallGraphRepairCandidates", label: "Wall repairs", layer: "wallGraphRepairs", geometry: "line" },
  { key: "routingLayer", label: "Routing layer" },
  { key: "objectGroups", label: "Object groups" },
  { key: "layers", label: "Source layers" }
];

const compareDrawableLayers = comparableLayers.filter((layer) => layer.layer && layer.geometry);

elements.fileInput.addEventListener("change", () => {
  const files = [...(elements.fileInput.files ?? [])];
  if (files.length) {
    loadFiles(files);
  }
});

elements.dropZone.addEventListener("dragover", (event) => {
  event.preventDefault();
  elements.dropZone.classList.add("dragging");
});

elements.dropZone.addEventListener("dragleave", () => {
  elements.dropZone.classList.remove("dragging");
});

elements.dropZone.addEventListener("drop", (event) => {
  event.preventDefault();
  elements.dropZone.classList.remove("dragging");
  const files = [...event.dataTransfer.files];
  if (files.length) {
    loadFiles(files);
  }
});

elements.prevPage.addEventListener("click", () => {
  if (state.currentPage > 1) {
    state.currentPage -= 1;
    renderCurrentPage();
  }
});

elements.nextPage.addEventListener("click", () => {
  if (state.currentPage < pageCount()) {
    state.currentPage += 1;
    renderCurrentPage();
  }
});

elements.downloadJson.addEventListener("click", () => {
  if (!state.scan && !state.visualSnapshot) {
    return;
  }

  const payload = state.placement ?? state.scan ?? state.visualSnapshot;
  const suffix = state.placement ? "placement" : state.visualSnapshot ? "visual-snapshot" : "scan";
  downloadBlob(
    new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-${suffix}.json`);
});

elements.downloadLayers.addEventListener("click", () => {
  if (!state.scan && !state.visualSnapshot) {
    return;
  }

  const layers = state.visualSnapshot
    ? visualSnapshotCurrentPage()?.layers ?? []
    : state.scan.layers ?? [];
  downloadBlob(
    new Blob([JSON.stringify(layers, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-layers.json`);
});

elements.downloadCalibration.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.calibration ?? {}, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-calibration.json`);
});

elements.downloadTitleBlocks.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.titleBlocks ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-title-blocks.json`);
});

elements.downloadDimensions.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.dimensions ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-dimensions.json`);
});

elements.downloadGridAxes.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.gridAxes ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-grid-axes.json`);
});

elements.downloadGridBays.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.gridBaySpacings ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-grid-bays.json`);
});

elements.downloadAnnotations.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.annotations ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-annotations.json`);
});

elements.downloadObjectGroups.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  downloadBlob(
    new Blob([JSON.stringify(state.scan.objectGroups ?? [], null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-object-groups.json`);
});

elements.downloadObjectLabelProfile.addEventListener("click", () => {
  if (state.kvemo) {
    const profile = buildKvemoObjectLabelProfileTemplate(state.kvemo);
    downloadBlob(
      new Blob([JSON.stringify(profile, null, 2)], { type: "application/json" }),
      `${safeKvemoName()}-object-label-profile.json`);
    return;
  }

  if (!state.scan) {
    return;
  }

  const profile = buildObjectLabelProfileTemplate(state.scan);
  downloadBlob(
    new Blob([JSON.stringify(profile, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-object-label-profile.json`);
});

elements.downloadObjectReviewDataset.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  const dataset = buildObjectReviewDataset(state.scan);
  downloadBlob(
    new Blob([JSON.stringify(dataset, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-object-review-dataset.json`);
});

elements.downloadObjectCorrectionDataset.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  const dataset = buildObjectCorrectionDataset(state.scan);
  downloadBlob(
    new Blob([JSON.stringify(dataset, null, 2)], { type: "application/json" }),
    `${safeDocumentName()}-object-corrections.json`);
});

elements.downloadBenchmark.addEventListener("click", () => {
  downloadReviewedBenchmarkManifest();
});

elements.downloadSvg.addEventListener("click", () => {
  if (!state.scan) {
    return;
  }

  const svg = serializeCurrentOverlaySvg();
  downloadBlob(
    new Blob([svg], { type: "image/svg+xml" }),
    `${safeDocumentName()}-page-${state.currentPage}-overlay.svg`);
});

document.querySelectorAll("[data-layer]").forEach((checkbox) => {
  checkbox.checked = state.enabledLayers.has(checkbox.dataset.layer);
  checkbox.addEventListener("change", () => {
    if (checkbox.checked) {
      state.enabledLayers.add(checkbox.dataset.layer);
    } else {
      state.enabledLayers.delete(checkbox.dataset.layer);
    }
    drawOverlay();
    setLegend();
    setAnalysisCounts(state.scan);
    refreshWorkspaceTabs();
  });
});

elements.applyDefaultLayers?.addEventListener("click", () => applyOverlayLayerPreset(defaultEnabledLayers));
elements.applyWallQaLayers?.addEventListener("click", () => applyOverlayLayerPreset(wallQaEnabledLayers));

function applyOverlayLayerPreset(layerKeys) {
  state.enabledLayers = new Set(layerKeys);
  syncLayerControls();
  drawOverlay();
  setLegend();
  setAnalysisCounts(state.scan);
  refreshWorkspaceTabs();
}

function syncLayerControls() {
  const availableLayerKeys = availableOverlayLayerKeys(state.scan);
  document.querySelectorAll("[data-layer]").forEach((checkbox) => {
    const layerKey = checkbox.dataset.layer;
    const available = availableLayerKeys.has(layerKey);
    checkbox.checked = state.enabledLayers.has(layerKey);
    checkbox.disabled = !available;
    const label = checkbox.closest("label");
    if (label) {
      label.hidden = !available && Boolean(state.placement);
      label.classList.toggle("disabled", !available);
    }
  });
}

elements.workspaceTabButtons.forEach((button) => {
  button.addEventListener("click", () => setWorkspaceTab(button.dataset.workspaceTab));
});

setWorkspaceTab(state.activeWorkspaceTab);
setCounts();
setCoordinateDetails();
setLegend();
setCursorCoordinates();
setAnalysisCounts();
setDiagnostics();
setCalibration();
setQuality();
setTitleBlocks();
setObjectGroups();
setCompare();
setBenchmarkDetails();
updateNavigation();
loadScanFromQueryString();

async function loadFiles(files) {
  const jsonl = files.find(isJsonLinesFile);
  if (jsonl) {
    await loadKvemoManifestFile(jsonl, files);
    return;
  }

  const pdf = files.find(isPdfFile);
  const json = files.find(isJsonFile);
  if (pdf && json) {
    await loadPdfAndScanJsonFiles(pdf, json);
    return;
  }

  if (json) {
    await loadScanJsonFile(json);
    return;
  }

  if (pdf) {
    await loadPdfFile(pdf);
  }
}

async function loadFile(file) {
  if (isJsonLinesFile(file)) {
    await loadKvemoManifestFile(file, [file]);
    return;
  }

  if (isJsonFile(file)) {
    await loadScanJsonFile(file);
    return;
  }

  await loadPdfFile(file);
}

async function loadPdfFile(file) {
  const operationRevision = beginViewerOperation();
  const documentRevision = beginDocumentLoad();
  const overlayRevision = beginBenchmarkOverlayLoad();
  state.pdf = null;
  state.scan = null;
  state.placement = null;
  state.currentPage = 1;
  state.sourceMode = "pdf";
  state.enabledSourceLayers = new Set();
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.benchmarkResult = null;
  state.benchmarkManifest = null;
  state.benchmarkTargets = [];
  state.benchmarkReviewDecisions = new Map();
  state.benchmarkTargetEdits = new Map();
  state.benchmarkDeletedTargets = new Set();
  state.benchmarkAddedTargetSequence = 1;
  state.pendingBenchmarkReviewSession = null;
  state.benchmarkFilters = resetBenchmarkFilters();
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;
  releaseKvemoObjectUrls();
  state.kvemo = null;
  state.kvemoFilters = resetKvemoFilters();
  state.selectedItem = null;
  elements.kvemoReview.hidden = true;
  elements.kvemoReview.replaceChildren();
  elements.fileMeta.textContent = `${file.name} - ${formatBytes(file.size)}`;
  setStatus("Loading PDF");
  setCounts();
  setDiagnostics();
  setSourceLayers();
  setCalibration();
  setQuality();
  setTitleBlocks();
  setObjectGroups();
  setCompare();
  setBenchmarkDetails();
  setSelection();
  clearOverlay();

  try {
    const [pdf] = await Promise.all([
      loadPdfForPreview(file),
      scanPdf(file, documentRevision, overlayRevision, operationRevision)
    ]);

    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    state.pdf = pdf;
    elements.emptyState.style.display = "none";
    elements.pageFrame.style.display = "block";
    await renderCurrentPage();
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadPdfAndScanJsonFiles(pdfFile, jsonFile) {
  const operationRevision = beginViewerOperation();
  const documentRevision = beginDocumentLoad();
  beginBenchmarkOverlayLoad();
  resetViewerState("pdf-json");
  elements.fileMeta.textContent = `${pdfFile.name} + ${jsonFile.name}`;
  setStatus("Loading PDF + JSON");

  try {
    const [pdf, payload] = await Promise.all([
      loadPdfForPreview(pdfFile),
      parseJsonFile(jsonFile)
    ]);

    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    state.pdf = pdf;
    state.compare = null;
    state.benchmarkComparison = null;
    state.batchComparison = null;
    if (!viewerOperationChangedSince(operationRevision)) {
      state.selectedItem = null;
    }

    if (isVisualSnapshotPayload(payload)) {
      state.scan = null;
      state.placement = null;
      state.visualSnapshot = normalizeVisualSnapshotPayload(payload, jsonFile.name);
      state.currentPage = state.visualSnapshot.pages[0]?.number ?? 1;
      state.sourceMode = "pdf-visual-snapshot";
      state.enabledSourceLayers = new Set();
      setCounts();
      setSourceLayers();
      setCalibration();
      setQuality(visualSnapshotQualityReport(state.visualSnapshot));
      setTitleBlocks();
      setObjectGroups();
      setDiagnostics(visualSnapshotDiagnostics(state.visualSnapshot));
    } else {
      state.visualSnapshot = null;
      state.placement = isPlacementPayload(payload) ? payload : null;
      state.scan = normalizeScanPayload(payload);
      state.currentPage = state.scan.pages[0]?.number ?? 1;
      state.sourceMode = state.placement ? "placement-pdf" : "pdf-json";
      state.enabledSourceLayers = new Set((state.scan.layers ?? []).map((layer) => layerKey(layer.name)));
      setCounts(state.scan);
      setSourceLayers(state.scan);
      setCalibration(state.scan.calibration, state.scan.measurementConsistency);
      setQuality(state.scan.quality);
      setTitleBlocks(state.scan);
      setObjectGroups(state.scan);
      setDiagnostics(state.scan.diagnostics);
    }

    setCompare();
    setBenchmarkDetails();
    setSelection();
    elements.emptyState.style.display = "none";
    elements.pageFrame.style.display = "block";
    await renderCurrentPage();
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Ready");
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setSourceUnderlayBadge();
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadScanJsonFile(file) {
  const operationRevision = beginViewerOperation();
  elements.fileMeta.textContent = `${file.name} - ${formatBytes(file.size)}`;
  setStatus("Loading JSON");

  try {
    const payload = JSON.parse(await file.text());
    if (isBenchmarkReviewSession(payload)) {
      loadBenchmarkReviewSessionPayload(payload, file.name);
      return;
    }

    if (isBenchmarkResult(payload)) {
      loadBenchmarkResultPayload(payload, file.name);
      return;
    }

    if (isBenchmarkComparison(payload)) {
      loadBenchmarkComparisonPayload(payload, file.name);
      return;
    }

    if (isBatchComparison(payload)) {
      loadBatchComparisonPayload(payload, file.name);
      return;
    }

    if (isVisualSnapshotPayload(payload)) {
      const documentRevision = beginDocumentLoad();
      resetViewerState("visual-snapshot");
      await loadVisualSnapshotPayload(payload, file.name, documentRevision);
      return;
    }

    if (isPlacementPayload(payload)) {
      const documentRevision = beginDocumentLoad();
      resetViewerState("placement");
      await loadPlacementPayload(payload, file.name, documentRevision);
      return;
    }

    if (isBenchmarkManifest(payload)) {
      loadBenchmarkManifestPayload(payload, file.name);
      return;
    }

    const documentRevision = beginDocumentLoad();
    const preserveBenchmark = Boolean(state.benchmarkManifest || state.benchmarkResult);
    resetViewerState("json", {
      preserveBenchmark,
      preserveSelection: preserveBenchmark && viewerOperationChangedSince(operationRevision)
    });
    const overlayRevision = state.benchmarkOverlayRevision;
    await loadScanPayload(payload, file.name, documentRevision, overlayRevision, operationRevision);
  } catch (error) {
    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadKvemoManifestFile(file, supportFiles = [file]) {
  beginViewerOperation();
  const documentRevision = beginDocumentLoad();
  elements.fileMeta.textContent = `${file.name} - ${formatBytes(file.size)}`;
  setStatus("Loading Kvemo");

  try {
    resetViewerState("kvemo");
    const imageFiles = buildKvemoImageFileMap(supportFiles);
    const { entries, invalidEntries } = parseKvemoManifest(await file.text(), imageFiles);
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    if (!entries.length) {
      throw new Error("Kvemo JSONL did not contain any valid crop entries.");
    }

    state.kvemo = {
      schemaVersion: currentKvemoCropSchemaVersion,
      manifestName: file.name,
      entries,
      invalidEntries,
      imageFileCount: imageFiles.size
    };
    state.kvemoFilters = resetKvemoFilters();
    elements.emptyState.style.display = "none";
    elements.pageFrame.style.display = "none";
    elements.kvemoReview.hidden = false;
    renderKvemoReview();
    setCounts();
    setSourceLayers();
    setCalibration();
    setQuality();
    setTitleBlocks();
    setObjectGroups();
    setCompare();
    setBenchmarkDetails();
    setDiagnostics(kvemoDiagnostics(state.kvemo));
    setSelection();
    updateNavigation();
    setStatus("Ready");
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    elements.kvemoReview.hidden = true;
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

function parseKvemoManifest(text, imageFiles) {
  const entries = [];
  const invalidEntries = [];
  text.split(/\r?\n/).forEach((line, lineIndex) => {
    const lineNumber = lineIndex + 1;
    if (!line.trim()) {
      return;
    }

    try {
      const raw = JSON.parse(line);
      if (!supportedKvemoCropSchemaVersions.has(raw?.schemaVersion)) {
        invalidEntries.push({ lineNumber, message: `Unexpected schemaVersion ${raw?.schemaVersion || "(missing)"}` });
        return;
      }

      entries.push(normalizeKvemoEntry(raw, entries.length + 1, lineNumber, imageFiles));
    } catch (error) {
      invalidEntries.push({ lineNumber, message: error.message || String(error) });
    }
  });

  return { entries, invalidEntries };
}

function normalizeKvemoEntry(raw, sequence, lineNumber, imageFiles) {
  const imageFileName = raw.imageFileName || fileNameFromPath(raw.imagePath) || `crop-${sequence}.png`;
  const localImage = imageFiles.get(imageFileName.toLowerCase());
  return {
    ...raw,
    sequence,
    lineNumber,
    id: raw.detectionId || `kvemo-crop-${sequence}`,
    reviewKey: raw.reviewKey || raw.groupSignature || raw.detectionId || `kvemo-crop-${sequence}`,
    groupSignature: raw.groupSignature || "",
    type: "kvemo crop",
    kind: raw.candidateKind || raw.detectionKind || "-",
    pageNumber: normalizedPageNumber(raw.pageNumber) ?? 1,
    bounds: normalizeRect(raw.bounds),
    cropBounds: normalizeRect(raw.cropBounds),
    reviewCropBounds: normalizeRect(raw.cropBounds),
    confidence: Number.isFinite(Number(raw.deterministicConfidence)) ? Number(raw.deterministicConfidence) : null,
    category: raw.category || "Unknown",
    imageFileName,
    imageUrl: localImage?.url || kvemoImageApiUrl(raw.imagePath),
    imageResolvedFromDrop: Boolean(localImage),
    detectedTags: normalizeStringArray(raw.detectedTags),
    nearbyText: normalizeStringArray(raw.nearbyText),
    sourcePrimitiveIds: normalizeStringArray(raw.sourcePrimitiveIds),
    sourceKind: raw.sourceKind || "Unknown",
    sourceWallComponentId: raw.sourceWallComponentId || null,
    sourceWallComponentKind: raw.sourceWallComponentKind || null,
    sourceKindCounts: normalizeKvemoCountArray(raw.sourceKindCounts, raw.sourceKind || "Unknown"),
    sourceWallComponentIds: normalizeStringArray(raw.sourceWallComponentIds ?? (raw.sourceWallComponentId ? [raw.sourceWallComponentId] : [])),
    sourceWallComponentKindCounts: normalizeKvemoCountArray(raw.sourceWallComponentKindCounts, raw.sourceWallComponentKind, false),
    sourceLayers: normalizeStringArray(raw.sourceEvidence?.layers),
    sourceEvidence: raw.sourceEvidence || {},
    evidence: normalizeStringArray(raw.evidence),
    reviewReasons: normalizeStringArray(raw.reviewReasons),
    visualAi: raw.classification || null,
    metadata: [
      raw.reviewPriority ? `priority ${raw.reviewPriority}` : "",
      raw.suggestedTrainingUse ? `training ${raw.suggestedTrainingUse}` : "",
      raw.reviewKey ? `review key ${raw.reviewKey}` : "",
      raw.objectToCropAreaRatio == null ? "" : `object/crop ${formatPercent(raw.objectToCropAreaRatio)}`,
      raw.sourceEvidence?.primitiveCount == null ? "" : `${raw.sourceEvidence.primitiveCount} source primitives`,
      localImage ? "image loaded from dropped file" : raw.imagePath ? "image served from local path" : "no image path"
    ].filter(Boolean).join(" | ")
  };
}

function renderKvemoReview() {
  if (!elements.kvemoReview || !state.kvemo) {
    return;
  }

  const entries = filteredKvemoEntries(state.kvemo);
  const summary = kvemoSummary(state.kvemo.entries);
  elements.kvemoReview.hidden = false;
  elements.kvemoReview.replaceChildren();

  const header = document.createElement("section");
  header.className = "kvemo-review-header";
  const title = document.createElement("div");
  title.innerHTML = `<strong>Kvemo crop review</strong><span>${escapeHtml(state.kvemo.manifestName)} - ${state.kvemo.entries.length} crop${state.kvemo.entries.length === 1 ? "" : "s"}</span>`;

  const stats = document.createElement("dl");
  [
    ["High", summary.high],
    ["Medium", summary.medium],
    ["Crop-only", summary.cropOnly],
    ["Classified", summary.classified],
    ["Sources", summary.sourceKindCount],
    ["Invalid", state.kvemo.invalidEntries.length]
  ].forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    stats.append(term, detail);
  });
  header.append(title, stats);

  const filters = renderKvemoFilters(entries.length);
  const grid = document.createElement("section");
  grid.className = "kvemo-crop-grid";
  entries.slice(0, 160).forEach((entry) => grid.appendChild(renderKvemoCropCard(entry)));
  if (entries.length > 160) {
    const overflow = document.createElement("div");
    overflow.className = "kvemo-crop-overflow";
    overflow.textContent = `${entries.length - 160} more crops hidden by the gallery limit; narrow the filters to inspect them.`;
    grid.appendChild(overflow);
  }

  elements.kvemoReview.append(header, filters, grid);
}

function renderKvemoFilters(filteredCount) {
  const filters = document.createElement("section");
  filters.className = "kvemo-filters";

  const query = document.createElement("input");
  query.type = "search";
  query.placeholder = "Search crops";
  query.value = state.kvemoFilters.query;
  query.addEventListener("input", () => {
    state.kvemoFilters.query = query.value;
    renderKvemoReview();
  });

  const priority = renderKvemoSelect(
    "priority",
    state.kvemoFilters.priority,
    ["all", ...uniqueValues(state.kvemo.entries.map((entry) => entry.reviewPriority))],
    (value) => {
      state.kvemoFilters.priority = value;
      renderKvemoReview();
    });

  const trainingUse = renderKvemoSelect(
    "training",
    state.kvemoFilters.trainingUse,
    ["all", ...uniqueValues(state.kvemo.entries.map((entry) => entry.suggestedTrainingUse))],
    (value) => {
      state.kvemoFilters.trainingUse = value;
      renderKvemoReview();
    });

  const sourceKind = renderKvemoSelect(
    "source",
    state.kvemoFilters.sourceKind,
    ["all", ...uniqueValues(state.kvemo.entries.flatMap((entry) => (entry.sourceKindCounts || []).map((item) => item.value)))],
    (value) => {
      state.kvemoFilters.sourceKind = value;
      renderKvemoReview();
    });

  const sourceWallComponentKind = renderKvemoSelect(
    "wall source",
    state.kvemoFilters.sourceWallComponentKind,
    ["all", ...uniqueValues(state.kvemo.entries.flatMap((entry) =>
      entry.sourceWallComponentKindCounts?.length
        ? entry.sourceWallComponentKindCounts.map((item) => item.value)
        : ["(none)"]))],
    (value) => {
      state.kvemoFilters.sourceWallComponentKind = value;
      renderKvemoReview();
    });

  const count = document.createElement("small");
  count.textContent = `${filteredCount} shown`;

  filters.append(query, priority, trainingUse, sourceKind, sourceWallComponentKind, count);
  return filters;
}

function renderKvemoSelect(label, value, options, onChange) {
  const select = document.createElement("select");
  select.title = label;
  options.forEach((option) => {
    const item = document.createElement("option");
    item.value = option || "all";
    item.textContent = option === "all" ? `All ${label}` : option;
    select.appendChild(item);
  });
  select.value = value;
  select.addEventListener("change", () => onChange(select.value));
  return select;
}

function renderKvemoCropCard(entry) {
  const card = document.createElement("button");
  card.type = "button";
  card.className = `kvemo-crop-card ${String(entry.reviewPriority || "").toLowerCase()}`;
  card.title = `Select ${entry.imageFileName}`;

  const media = document.createElement("span");
  media.className = "kvemo-crop-media";
  if (entry.imageUrl) {
    const image = document.createElement("img");
    image.alt = entry.imageFileName;
    image.loading = "lazy";
    image.src = entry.imageUrl;
    image.addEventListener("error", () => {
      media.classList.add("missing");
      media.textContent = "image unavailable";
    }, { once: true });
    media.appendChild(image);
  } else {
    media.classList.add("missing");
    media.textContent = "no image";
  }

  const title = document.createElement("strong");
  title.textContent = entry.label || entry.symbolName || entry.category || entry.detectionId || entry.imageFileName;

  const meta = document.createElement("span");
  meta.textContent = [
    entry.reviewPriority || "priority -",
    entry.suggestedTrainingUse || "training -",
    `source ${formatKvemoCountSummary(entry.sourceKindCounts)}`,
    entry.sourceWallComponentKindCounts.length ? `wall ${formatKvemoCountSummary(entry.sourceWallComponentKindCounts)}` : "",
    entry.groupSignature ? "grouped" : "",
    entry.category || "",
    entry.confidence == null ? "" : formatPercent(entry.confidence)
  ].filter(Boolean).join(" - ");

  const reason = document.createElement("small");
  reason.textContent = entry.reviewReasons.slice(0, 2).join(" - ");

  card.append(media, title, meta, reason);
  card.addEventListener("click", () => {
    state.selectedItem = describeKvemoEntry(entry);
    setSelection(state.selectedItem);
  });
  return card;
}

function describeKvemoEntry(entry) {
  return {
    ...entry,
    type: "kvemo crop",
    id: entry.detectionId,
    reviewKey: entry.reviewKey,
    groupSignature: entry.groupSignature,
    kind: entry.candidateKind || entry.detectionKind,
    topology: entry.detectionKind,
    pageNumber: entry.pageNumber,
    bounds: entry.bounds,
    reviewCropBounds: entry.cropBounds,
    sourceLayers: entry.sourceEvidence?.layers || [],
    sourceKind: entry.sourceKind || "Unknown",
    sourceWallComponentId: entry.sourceWallComponentId || null,
    sourceWallComponentKind: entry.sourceWallComponentKind || null,
    sourceKindCounts: entry.sourceKindCounts || [],
    sourceWallComponentIds: entry.sourceWallComponentIds || [],
    sourceWallComponentKindCounts: entry.sourceWallComponentKindCounts || [],
    sourcePrimitiveIds: entry.sourcePrimitiveIds,
    evidence: entry.evidence,
    nearbyText: entry.nearbyText,
    metadata: entry.metadata
  };
}

function filteredKvemoEntries(kvemo) {
  const query = state.kvemoFilters.query.trim().toLowerCase();
  return kvemo.entries
    .filter((entry) => state.kvemoFilters.priority === "all" || entry.reviewPriority === state.kvemoFilters.priority)
    .filter((entry) => state.kvemoFilters.trainingUse === "all" || entry.suggestedTrainingUse === state.kvemoFilters.trainingUse)
    .filter((entry) => state.kvemoFilters.sourceKind === "all" || kvemoEntryHasCountValue(entry.sourceKindCounts, state.kvemoFilters.sourceKind))
    .filter((entry) => state.kvemoFilters.sourceWallComponentKind === "all" || kvemoEntryHasWallComponentKind(entry, state.kvemoFilters.sourceWallComponentKind))
    .filter((entry) => !query || kvemoSearchText(entry).includes(query))
    .sort(compareKvemoEntries);
}

function compareKvemoEntries(first, second) {
  const priorityDelta = kvemoPriorityRank(second.reviewPriority) - kvemoPriorityRank(first.reviewPriority);
  if (priorityDelta !== 0) {
    return priorityDelta;
  }

  const sourceDelta = (second.sourceEvidence?.primitiveCount ?? 0) - (first.sourceEvidence?.primitiveCount ?? 0);
  if (sourceDelta !== 0) {
    return sourceDelta;
  }

  return first.sequence - second.sequence;
}

function kvemoPriorityRank(priority) {
  switch (String(priority || "").toLowerCase()) {
    case "high":
      return 3;
    case "medium":
      return 2;
    case "low":
      return 1;
    default:
      return 0;
  }
}

function kvemoSearchText(entry) {
  return [
    entry.detectionId,
    entry.reviewKey,
    entry.groupSignature,
    entry.detectionKind,
    entry.category,
    entry.candidateKind,
    entry.sourceKind,
    entry.sourceWallComponentId,
    entry.sourceWallComponentKind,
    formatKvemoCountSummary(entry.sourceKindCounts),
    formatKvemoCountSummary(entry.sourceWallComponentKindCounts),
    ...(entry.sourceWallComponentIds || []),
    entry.label,
    entry.symbolName,
    entry.imageFileName,
    entry.reviewPriority,
    entry.suggestedTrainingUse,
    ...(entry.reviewReasons || []),
    ...(entry.nearbyText || []),
    ...(entry.sourceEvidence?.layers || []),
    ...(entry.sourceEvidence?.entityTypes || []),
    ...(entry.sourceEvidence?.blockNames || [])
  ].filter(Boolean).join(" ").toLowerCase();
}

function buildKvemoImageFileMap(files) {
  const map = new Map();
  files.filter(isKvemoImageFile).forEach((file) => {
    map.set(file.name.toLowerCase(), {
      name: file.name,
      url: URL.createObjectURL(file)
    });
  });
  return map;
}

function releaseKvemoObjectUrls() {
  for (const entry of state.kvemo?.entries ?? []) {
    if (entry.imageResolvedFromDrop && entry.imageUrl) {
      URL.revokeObjectURL(entry.imageUrl);
    }
  }
}

function kvemoImageApiUrl(path) {
  if (!path) {
    return "";
  }

  return `/api/kvemo-crop-image?path=${encodeURIComponent(path)}`;
}

function fileNameFromPath(path) {
  if (!path) {
    return "";
  }

  return String(path).split(/[\\/]/).filter(Boolean).at(-1) || "";
}

function resetKvemoFilters() {
  return { ...defaultKvemoFilters };
}

function normalizeKvemoCountArray(values, fallbackValue, includeFallback = true) {
  const counts = new Map();
  const items = Array.isArray(values) ? values : [];
  items.forEach((item) => {
    const value = String(item?.value || "").trim();
    const count = Number(item?.count);
    if (!value || !Number.isFinite(count) || count <= 0) {
      return;
    }

    counts.set(value, (counts.get(value) || 0) + count);
  });

  if (counts.size === 0 && includeFallback && fallbackValue) {
    counts.set(String(fallbackValue).trim(), 1);
  }

  return [...counts.entries()]
    .map(([value, count]) => ({ value, count }))
    .sort((first, second) => second.count - first.count || first.value.localeCompare(second.value));
}

function formatKvemoCountSummary(counts) {
  const items = Array.isArray(counts) ? counts : [];
  return items.length
    ? items.map((item) => `${item.value}:${item.count}`).join(", ")
    : "-";
}

function kvemoEntryHasCountValue(counts, value) {
  return (counts || []).some((item) => item.value === value);
}

function kvemoEntryHasWallComponentKind(entry, value) {
  if (value === "(none)") {
    return !entry.sourceWallComponentKindCounts?.length;
  }

  return kvemoEntryHasCountValue(entry.sourceWallComponentKindCounts, value);
}

function kvemoDiagnostics(kvemo) {
  return {
    infoCount: 1,
    warningCount: kvemo.invalidEntries.length ? 1 : 0,
    errorCount: 0,
    durationMilliseconds: 0,
    stages: [],
    messages: [
      {
        code: "kvemo.manifest.loaded",
        severity: "Info",
        stage: "viewer",
        scope: "Document",
        message: `Loaded ${kvemo.entries.length} Kvemo crop entries.`,
        confidence: 1,
        properties: {
          imageFileCount: `${kvemo.imageFileCount}`,
          invalidEntryCount: `${kvemo.invalidEntries.length}`
        }
      },
      ...kvemo.invalidEntries.slice(0, 8).map((entry) => ({
        code: "kvemo.manifest.invalid_line",
        severity: "Warning",
        stage: "viewer",
        scope: "Document",
        message: `Line ${entry.lineNumber}: ${entry.message}`,
        confidence: 1,
        properties: {}
      }))
    ]
  };
}

function kvemoSummary(entries) {
  return {
    high: entries.filter((entry) => entry.reviewPriority === "High").length,
    medium: entries.filter((entry) => entry.reviewPriority === "Medium").length,
    cropOnly: entries.filter((entry) => !entry.visualAi).length,
    classified: entries.filter((entry) => entry.visualAi).length,
    sourceKindCount: uniqueValues(entries.flatMap((entry) => (entry.sourceKindCounts || []).map((item) => item.value))).length
  };
}

function kvemoCountRows(kvemo) {
  const summary = kvemoSummary(kvemo.entries);
  return [
    ["Crops", kvemo.entries.length],
    ["High priority", summary.high],
    ["Medium priority", summary.medium],
    ["Crop-only", summary.cropOnly],
    ["Classified", summary.classified],
    ["Source kinds", summary.sourceKindCount],
    ["Invalid lines", kvemo.invalidEntries.length],
    ["Image files", kvemo.imageFileCount]
  ];
}

function kvemoAnalysisRows(kvemo) {
  const pages = uniqueValues(kvemo.entries.map((entry) => entry.pageNumber));
  const sourcePrimitiveTotal = kvemo.entries.reduce((sum, entry) => sum + (entry.sourceEvidence?.primitiveCount ?? 0), 0);
  const avgRatio = kvemo.entries.length
    ? kvemo.entries.reduce((sum, entry) => sum + Number(entry.objectToCropAreaRatio || 0), 0) / kvemo.entries.length
    : 0;
  return [
    ["Current page", "-"],
    ["Page size", "-"],
    ["Visible layers", "-"],
    ["Visible items", filteredKvemoEntries(kvemo).length],
    ["All detections", kvemo.entries.length],
    ["Source layers", kvemoSourceLayerCounts(kvemo).length],
    ["Diagnostics", kvemo.invalidEntries.length],
    ["Quality", `crop review (${formatPercent(avgRatio)})`],
    ["Pages", pages.length],
    ["Source IDs", sourcePrimitiveTotal]
  ];
}

function batchComparisonCountRows(comparison) {
  return [
    ["Items", comparison.items.length],
    ["Matched", comparison.matchedItemCount],
    ["Added", comparison.addedItemCount],
    ["Removed", comparison.removedItemCount],
    ["Regressions", comparison.regressionCount],
    ["Improvements", comparison.improvementCount],
    ["Info", comparison.infoCount],
    ["Visual delta", formatSigned(comparison.visualIssueDelta)],
    ["Diagnostic err delta", formatSigned(comparison.diagnosticErrorDelta)],
    ["Duration delta", formatMilliseconds(comparison.totalDurationDeltaMilliseconds)]
  ];
}

function benchmarkComparisonCountRows(comparison) {
  return [
    ["Cases", comparison.cases.length],
    ["Matched", comparison.matchedCaseCount],
    ["Added", comparison.addedCaseCount],
    ["Removed", comparison.removedCaseCount],
    ["Regressions", comparison.regressionCount],
    ["Improvements", comparison.improvementCount],
    ["Info", comparison.infoCount],
    ["Failed cases", comparison.cases.filter(benchmarkComparisonCaseFailed).length],
    ["Skipped cases", comparison.cases.filter(benchmarkComparisonCaseSkipped).length]
  ];
}

function batchComparisonAnalysisRows(comparison, visibleLayerCount) {
  return [
    ["Current page", "-"],
    ["Page size", "-"],
    ["Visible layers", `${visibleLayerCount} / ${overlayLegendItems.length}`],
    ["Visible items", comparison.items.length],
    ["All detections", comparison.signals.length],
    ["Source layers", 0],
    ["Diagnostics", comparison.signals.length],
    ["Quality", comparison.passed ? "pass" : "regression"],
    ["Matched", comparison.matchedItemCount],
    ["Evidence items", comparison.items.filter(batchComparisonItemHasEvidence).length]
  ];
}

function benchmarkComparisonAnalysisRows(comparison, visibleLayerCount) {
  return [
    ["Current page", "-"],
    ["Page size", "-"],
    ["Visible layers", `${visibleLayerCount} / ${overlayLegendItems.length}`],
    ["Visible items", comparison.cases.length],
    ["All detections", comparison.signals.length],
    ["Source layers", 0],
    ["Diagnostics", comparison.signals.length],
    ["Quality", comparison.passed ? "pass" : "regression"],
    ["Matched", comparison.matchedCaseCount],
    ["Failed cases", comparison.cases.filter(benchmarkComparisonCaseFailed).length]
  ];
}

function renderKvemoLegend(kvemo) {
  const wrapper = document.createElement("div");
  wrapper.className = "kvemo-legend";
  [
    ["High", kvemo.entries.filter((entry) => entry.reviewPriority === "High").length, "#c43d3d"],
    ["Medium", kvemo.entries.filter((entry) => entry.reviewPriority === "Medium").length, "#c97c18"],
    ["Low", kvemo.entries.filter((entry) => entry.reviewPriority === "Low").length, "#3f8f57"]
  ].forEach(([label, count, color]) => {
    const row = document.createElement("div");
    row.className = "legend-row";
    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";
    swatch.style.borderColor = color;
    swatch.style.background = `${color}1a`;
    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = `${label} priority`;
    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;
    row.append(swatch, text, meta);
    wrapper.appendChild(row);
  });
  kvemoSourceKindCounts(kvemo).slice(0, 6).forEach(([label, count]) => {
    const row = document.createElement("div");
    row.className = "legend-row";
    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";
    swatch.style.borderColor = "#7854a8";
    swatch.style.background = "rgba(120, 84, 168, 0.10)";
    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = `Source ${label}`;
    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;
    row.append(swatch, text, meta);
    wrapper.appendChild(row);
  });
  return wrapper;
}

function renderKvemoSourceEvidence(kvemo) {
  const layerCounts = kvemoSourceLayerCounts(kvemo);
  const sourceKindCounts = kvemoSourceKindCounts(kvemo);
  const wallComponentKindCounts = kvemoWallComponentKindCounts(kvemo);
  if (!layerCounts.length && !sourceKindCounts.length && !wallComponentKindCounts.length) {
    const empty = document.createElement("div");
    empty.textContent = "No source evidence in manifest";
    return [empty];
  }

  const rows = [];
  sourceKindCounts.slice(0, 12).forEach(([kind, count]) => {
    rows.push(renderKvemoSourceEvidenceRow(kind, count, "Source kind"));
  });
  wallComponentKindCounts.slice(0, 12).forEach(([kind, count]) => {
    rows.push(renderKvemoSourceEvidenceRow(kind, count, "Wall component"));
  });
  layerCounts.slice(0, 40).forEach(([layer, count]) => {
    rows.push(renderKvemoSourceEvidenceRow(layer, count, "Kvemo layer"));
  });
  return rows;
}

function renderKvemoSourceEvidenceRow(label, count, categoryLabel) {
  const row = document.createElement("div");
  row.className = "source-layer";
  const spacer = document.createElement("span");
  const text = document.createElement("span");
  text.innerHTML = `<strong>${escapeHtml(label)}</strong><span>${count} crop${count === 1 ? "" : "s"} reference this evidence</span>`;
  const category = document.createElement("em");
  category.textContent = categoryLabel;
  row.append(spacer, text, category);
  return row;
}

function renderKvemoTopEntries(kvemo) {
  return filteredKvemoEntries(kvemo).slice(0, 30).map((entry) => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = `object-group ${entry.reviewPriority === "High" ? "review" : ""}`;
    const title = document.createElement("strong");
    title.textContent = entry.label || entry.symbolName || entry.imageFileName;
    const detail = document.createElement("span");
    detail.textContent = [
      entry.reviewPriority,
      entry.suggestedTrainingUse,
      entry.sourceKindCounts?.length ? `source ${formatKvemoCountSummary(entry.sourceKindCounts)}` : "",
      entry.category,
      `${entry.sourceEvidence?.primitiveCount ?? 0} sources`
    ].filter(Boolean).join(" - ");
    const meta = document.createElement("small");
    meta.textContent = entry.reviewReasons.slice(0, 2).join(" - ");
    item.append(title, detail, meta);
    item.addEventListener("click", () => {
      state.selectedItem = describeKvemoEntry(entry);
      setSelection(state.selectedItem);
    });
    return item;
  });
}

function kvemoSourceLayerCounts(kvemo) {
  const counts = new Map();
  kvemo.entries.forEach((entry) => {
    (entry.sourceEvidence?.layers ?? []).forEach((layer) => {
      counts.set(layer, (counts.get(layer) ?? 0) + 1);
    });
  });
  return [...counts.entries()].sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]));
}

function kvemoSourceKindCounts(kvemo) {
  const counts = new Map();
  kvemo.entries.forEach((entry) => {
    (entry.sourceKindCounts?.length ? entry.sourceKindCounts : [{ value: entry.sourceKind || "Unknown", count: 1 }])
      .forEach((item) => counts.set(item.value, (counts.get(item.value) ?? 0) + item.count));
  });
  return [...counts.entries()].sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]));
}

function kvemoWallComponentKindCounts(kvemo) {
  const counts = new Map();
  kvemo.entries.forEach((entry) => {
    (entry.sourceWallComponentKindCounts ?? [])
      .forEach((item) => counts.set(item.value, (counts.get(item.value) ?? 0) + item.count));
  });
  return [...counts.entries()].sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]));
}

function uniqueValues(values) {
  return [...new Set(values.filter((value) => value !== undefined && value !== null && value !== ""))];
}

async function loadScanFromQueryString() {
  const params = new URLSearchParams(window.location.search);
  const kvemoUrl = params.get("kvemo") ?? params.get("kvemoManifest") ?? params.get("crops");
  const pdfUrl = params.get("pdf") ?? params.get("sourcePdf") ?? params.get("source");
  const placementUrl = params.get("placement") ?? params.get("placementJson") ?? params.get("placementPacket");
  const snapshotUrl = params.get("snapshot") ?? params.get("visualSnapshot") ?? params.get("visual");
  const scanUrl = params.get("scan");
  const benchmarkUrl = params.get("benchmark") ?? params.get("manifest") ?? params.get("draft");
  const benchmarkResultUrl = params.get("benchmarkResult") ?? params.get("result") ?? params.get("run");
  const benchmarkComparisonUrl = params.get("benchmarkComparison") ?? params.get("benchmarkCompare") ?? params.get("benchmarkComparisonResult");
  const batchComparisonUrl = params.get("batchComparison") ?? params.get("batchCompare") ?? params.get("batchComparisonResult");
  const reviewSessionUrl = params.get("session") ?? params.get("reviewSession");
  const baselineUrl = params.get("baseline") ?? params.get("left") ?? ((scanUrl && params.get("compare")) ? params.get("compare") : null);
  const candidateUrl = params.get("candidate") ?? params.get("right") ?? scanUrl;
  if (kvemoUrl) {
    await loadKvemoManifestFromUrl(kvemoUrl);
    return;
  }

  if (benchmarkComparisonUrl) {
    await loadBenchmarkComparisonFromUrl(benchmarkComparisonUrl);
    return;
  }

  if (batchComparisonUrl) {
    await loadBatchComparisonFromUrl(batchComparisonUrl);
    return;
  }

  if (placementUrl && !pdfUrl) {
    await loadPlacementFromUrl(placementUrl);
    return;
  }

  if (snapshotUrl && !pdfUrl) {
    await loadVisualSnapshotFromUrl(snapshotUrl);
    return;
  }

  if (pdfUrl && !baselineUrl) {
    await loadPdfFromUrl(
      pdfUrl,
      placementUrl ? resolveJsonArtifactUrl(placementUrl) : candidateUrl ? resolveJsonArtifactUrl(candidateUrl) : candidateUrl,
      benchmarkUrl,
      reviewSessionUrl,
      benchmarkResultUrl);
    return;
  }

  if (baselineUrl && candidateUrl) {
    await loadScanComparisonFromUrls(baselineUrl, candidateUrl);
    if (benchmarkUrl) {
      await tryLoadBenchmarkManifestFromUrl(benchmarkUrl);
    }
    if (reviewSessionUrl) {
      await tryLoadBenchmarkReviewSessionFromUrl(reviewSessionUrl);
    }
    if (benchmarkResultUrl) {
      await tryLoadBenchmarkResultFromUrl(benchmarkResultUrl);
    }
    return;
  }

  if (!candidateUrl) {
    if (benchmarkResultUrl) {
      await tryLoadBenchmarkResultFromUrl(benchmarkResultUrl);
    }
    return;
  }

  resetViewerState("json");
  elements.fileMeta.textContent = "Loading scan JSON from URL";
  setStatus("Loading JSON");

  try {
    const resolvedCandidateUrl = resolveJsonArtifactUrl(candidateUrl);
    await loadScanPayload(await fetchJson(resolvedCandidateUrl), candidateUrl);
    if (benchmarkUrl) {
      await tryLoadBenchmarkManifestFromUrl(benchmarkUrl);
    }
    if (reviewSessionUrl) {
      await tryLoadBenchmarkReviewSessionFromUrl(reviewSessionUrl);
    }
    if (benchmarkResultUrl) {
      await tryLoadBenchmarkResultFromUrl(benchmarkResultUrl);
    }
  } catch (error) {
    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadVisualSnapshotFromUrl(snapshotUrl) {
  const documentRevision = beginDocumentLoad();
  resetViewerState("visual-snapshot");
  const label = cleanUrlLabel(snapshotUrl);
  elements.fileMeta.textContent = `Loading ${label}`;
  setStatus("Loading visual snapshot");

  try {
    await loadVisualSnapshotPayload(
      await fetchJson(resolveJsonArtifactUrl(snapshotUrl)),
      label,
      documentRevision);
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadKvemoManifestFromUrl(kvemoUrl) {
  resetViewerState("kvemo");
  const label = cleanUrlLabel(kvemoUrl);
  elements.fileMeta.textContent = `Loading ${label}`;
  setStatus("Loading Kvemo");

  try {
    const text = await fetchText(resolveKvemoManifestUrl(kvemoUrl));
    const { entries, invalidEntries } = parseKvemoManifest(text, new Map());
    if (!entries.length) {
      throw new Error("Kvemo JSONL did not contain any valid crop entries.");
    }

    state.kvemo = {
      schemaVersion: currentKvemoCropSchemaVersion,
      manifestName: label,
      entries,
      invalidEntries,
      imageFileCount: 0
    };
    state.kvemoFilters = resetKvemoFilters();
    elements.emptyState.style.display = "none";
    elements.pageFrame.style.display = "none";
    elements.kvemoReview.hidden = false;
    renderKvemoReview();
    setCounts();
    setSourceLayers();
    setCalibration();
    setQuality();
    setTitleBlocks();
    setObjectGroups();
    setCompare();
    setBenchmarkDetails();
    setDiagnostics(kvemoDiagnostics(state.kvemo));
    setSelection();
    updateNavigation();
    elements.fileMeta.textContent = `${label} - ${entries.length} crop${entries.length === 1 ? "" : "s"}`;
    setStatus("Ready");
  } catch (error) {
    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    elements.kvemoReview.hidden = true;
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadPdfFromUrl(pdfUrl, scanUrl, benchmarkUrl, reviewSessionUrl, benchmarkResultUrl) {
  const operationRevision = beginViewerOperation();
  const documentRevision = beginDocumentLoad();
  resetViewerState("pdf");
  elements.fileMeta.textContent = "Loading PDF from URL";
  setStatus("Loading PDF");

  try {
    const file = await fetchFile(resolvePdfArtifactUrl(pdfUrl), cleanUrlLabel(pdfUrl), "application/pdf");
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    elements.fileMeta.textContent = `${file.name} - ${formatBytes(file.size)}`;

    if (scanUrl) {
      const [pdf, payload] = await Promise.all([
        loadPdfForPreview(file),
        fetchJson(scanUrl)
      ]);

      if (!isCurrentDocumentLoad(documentRevision)) {
        return;
      }

      state.pdf = pdf;
      state.placement = isPlacementPayload(payload) ? payload : null;
      state.scan = normalizeScanPayload(payload);
      state.currentPage = state.scan.pages[0]?.number ?? 1;
      state.sourceMode = state.placement ? "placement-pdf" : "pdf-json";
      state.enabledSourceLayers = new Set((state.scan.layers ?? []).map((layer) => layerKey(layer.name)));
      setCounts(state.scan);
      setSourceLayers(state.scan);
      setCalibration(state.scan.calibration, state.scan.measurementConsistency);
      setQuality(state.scan.quality);
      setTitleBlocks(state.scan);
      setObjectGroups(state.scan);
      setCompare();
      setBenchmarkDetails();
      setDiagnostics(state.scan.diagnostics);
      setSelection();
    } else {
      const [pdf] = await Promise.all([
        loadPdfForPreview(file),
        scanPdf(file, documentRevision, state.benchmarkOverlayRevision, operationRevision)
      ]);
      if (!isCurrentDocumentLoad(documentRevision)) {
        return;
      }

      state.pdf = pdf;
    }

    if (benchmarkUrl) {
      await tryLoadBenchmarkManifestFromUrl(benchmarkUrl);
    }
    if (reviewSessionUrl) {
      await tryLoadBenchmarkReviewSessionFromUrl(reviewSessionUrl);
    }
    if (benchmarkResultUrl) {
      await tryLoadBenchmarkResultFromUrl(benchmarkResultUrl);
    }

    elements.emptyState.style.display = "none";
    elements.pageFrame.style.display = "block";
    await renderCurrentPage();
    setStatus("Ready");
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadScanPayload(
  payload,
  label,
  documentRevision = state.documentLoadRevision,
  overlayRevision = state.benchmarkOverlayRevision,
  operationRevision = state.viewerOperationRevision) {
  if (isPlacementPayload(payload)) {
    await loadPlacementPayload(payload, label, documentRevision, overlayRevision, operationRevision);
    return;
  }

  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  state.pdf = null;
  state.placement = null;
  state.scan = normalizeScanPayload(payload);
  state.currentPage = state.scan.pages[0]?.number ?? 1;
  state.sourceMode = "json";
  state.enabledSourceLayers = new Set((state.scan.layers ?? []).map((layer) => layerKey(layer.name)));
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    state.selectedItem = null;
  }

  const displayLabel = label.startsWith("data:") ? "scan JSON URL" : label;

  setCounts(state.scan);
  setSourceLayers(state.scan);
  setCalibration(state.scan.calibration, state.scan.measurementConsistency);
  setQuality(state.scan.quality);
  setTitleBlocks(state.scan);
  setObjectGroups(state.scan);
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(state.scan.diagnostics);
  setSelection(
    benchmarkOverlayChangedSince(overlayRevision) || viewerOperationChangedSince(operationRevision)
      ? state.selectedItem
      : null);
  elements.fileMeta.textContent = `${displayLabel} - ${state.scan.pages.length} page${state.scan.pages.length === 1 ? "" : "s"}`;
  elements.emptyState.style.display = "none";
  elements.pageFrame.style.display = "block";
  await renderCurrentPage({
    preserveStatus: benchmarkOverlayChangedSince(overlayRevision) || viewerOperationChangedSince(operationRevision)
  });
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    setStatus("Ready");
  }
}

async function loadVisualSnapshotPayload(payload, label, documentRevision = state.documentLoadRevision) {
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  state.pdf = null;
  state.scan = null;
  state.placement = null;
  state.visualSnapshot = normalizeVisualSnapshotPayload(payload, label);
  state.currentPage = state.visualSnapshot.pages[0]?.number ?? 1;
  state.sourceMode = "visual-snapshot";
  state.enabledSourceLayers = new Set();
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.selectedItem = null;

  const displayLabel = label.startsWith("data:") ? "visual snapshot URL" : label;

  setCounts();
  setSourceLayers();
  setCalibration();
  setQuality(visualSnapshotQualityReport(state.visualSnapshot));
  setTitleBlocks();
  setObjectGroups();
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(visualSnapshotDiagnostics(state.visualSnapshot));
  setSelection();
  elements.fileMeta.textContent = `${displayLabel} - ${state.visualSnapshot.pages.length} snapshot page${state.visualSnapshot.pages.length === 1 ? "" : "s"}`;
  elements.emptyState.style.display = "none";
  elements.pageFrame.style.display = "block";
  await renderCurrentPage();
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  setStatus("Ready");
}

async function loadPlacementFromUrl(placementUrl) {
  const documentRevision = beginDocumentLoad();
  resetViewerState("placement");
  const label = cleanUrlLabel(placementUrl);
  elements.fileMeta.textContent = `Loading ${label}`;
  setStatus("Loading placement");

  try {
    await loadPlacementPayload(
      await fetchJson(resolveJsonArtifactUrl(placementUrl)),
      label,
      documentRevision);
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadPlacementPayload(
  payload,
  label,
  documentRevision = state.documentLoadRevision,
  overlayRevision = state.benchmarkOverlayRevision,
  operationRevision = state.viewerOperationRevision) {
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  state.pdf = null;
  state.placement = payload;
  state.scan = normalizePlacementPayload(payload);
  state.currentPage = state.scan.pages[0]?.number ?? 1;
  state.sourceMode = "placement";
  state.enabledSourceLayers = new Set((state.scan.layers ?? []).map((layer) => layerKey(layer.name)));
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    state.selectedItem = null;
  }

  const displayLabel = label.startsWith("data:") ? "placement JSON URL" : label;

  setCounts(state.scan);
  setSourceLayers(state.scan);
  setCalibration(state.scan.calibration, state.scan.measurementConsistency);
  setQuality(state.scan.quality);
  setTitleBlocks(state.scan);
  setObjectGroups(state.scan);
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(state.scan.diagnostics);
  setSelection(
    benchmarkOverlayChangedSince(overlayRevision) || viewerOperationChangedSince(operationRevision)
      ? state.selectedItem
      : null);
  elements.fileMeta.textContent = `${displayLabel} - placement - ${state.scan.pages.length} page${state.scan.pages.length === 1 ? "" : "s"}`;
  elements.emptyState.style.display = "none";
  elements.pageFrame.style.display = "block";
  await renderCurrentPage({
    preserveStatus: benchmarkOverlayChangedSince(overlayRevision) || viewerOperationChangedSince(operationRevision)
  });
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    setStatus("Placement ready");
  }
}

async function loadScanComparisonFromUrls(baselineUrl, candidateUrl) {
  const documentRevision = beginDocumentLoad();
  resetViewerState("compare");
  elements.fileMeta.textContent = "Loading scan comparison";
  setStatus("Loading Compare");

  try {
    const resolvedBaselineUrl = resolveJsonArtifactUrl(baselineUrl);
    const resolvedCandidateUrl = resolveJsonArtifactUrl(candidateUrl);
    const [baselinePayload, candidatePayload] = await Promise.all([
      fetchJson(resolvedBaselineUrl),
      fetchJson(resolvedCandidateUrl)
    ]);

    await loadScanComparisonPayloads(baselinePayload, candidatePayload, baselineUrl, candidateUrl, documentRevision);
  } catch (error) {
    if (!isCurrentDocumentLoad(documentRevision)) {
      return;
    }

    setStatus("Failed");
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

async function loadScanComparisonPayloads(
  baselinePayload,
  candidatePayload,
  baselineLabel,
  candidateLabel,
  documentRevision = state.documentLoadRevision) {
  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  const baseline = normalizeScanPayload(baselinePayload);
  const candidate = normalizeScanPayload(candidatePayload);

  state.pdf = null;
  state.placement = null;
  state.scan = candidate;
  state.currentPage = candidate.pages[0]?.number ?? baseline.pages[0]?.number ?? 1;
  state.sourceMode = "compare";
  state.compare = buildScanComparison(baseline, candidate, cleanUrlLabel(baselineLabel), cleanUrlLabel(candidateLabel));
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.enabledSourceLayers = new Set(mergedSourceLayers(baseline, candidate).map((layer) => layerKey(layer.name)));
  beginBenchmarkOverlayLoad();
  state.benchmarkResult = null;
  state.benchmarkManifest = null;
  state.benchmarkTargets = [];
  state.benchmarkReviewDecisions = new Map();
  state.benchmarkTargetEdits = new Map();
  state.benchmarkDeletedTargets = new Set();
  state.benchmarkAddedTargetSequence = 1;
  state.pendingBenchmarkReviewSession = null;
  state.benchmarkFilters = resetBenchmarkFilters();
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;
  state.selectedItem = null;

  setCounts(candidate);
  setSourceLayers({ layers: mergedSourceLayers(baseline, candidate) });
  setCalibration(candidate.calibration, candidate.measurementConsistency);
  setQuality(candidate.quality);
  setTitleBlocks(candidate);
  setObjectGroups(candidate);
  setCompare(state.compare);
  setBenchmarkDetails();
  setDiagnostics(candidate.diagnostics);
  setSelection();

  elements.fileMeta.textContent = `${state.compare.candidateLabel} vs ${state.compare.baselineLabel} - ${candidate.pages.length} page${candidate.pages.length === 1 ? "" : "s"}`;
  elements.emptyState.style.display = "none";
  elements.pageFrame.style.display = "block";
  await renderCurrentPage();
  setStatus("Ready");
}

async function fetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Could not load JSON: ${response.status}`);
  }

  return response.json();
}

async function fetchText(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Could not load text: ${response.status}`);
  }

  return response.text();
}

function resolveKvemoManifestUrl(value) {
  if (/^https?:\/\//i.test(value) || value.startsWith("./") || value.startsWith("/") || value.startsWith("data:")) {
    return value;
  }

  return `/api/kvemo-crop-manifest?path=${encodeURIComponent(value)}`;
}

function resolveJsonArtifactUrl(value) {
  if (/^https?:\/\//i.test(value) || value.startsWith("./") || value.startsWith("/") || value.startsWith("data:")) {
    return value;
  }

  return `/api/json-file?path=${encodeURIComponent(value)}`;
}

function resolvePdfArtifactUrl(value) {
  if (/^https?:\/\//i.test(value) || value.startsWith("./") || value.startsWith("/") || value.startsWith("data:")) {
    return value;
  }

  return `/api/pdf-file?path=${encodeURIComponent(value)}`;
}

async function fetchFile(url, fallbackName, fallbackType) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Could not load file: ${response.status}`);
  }

  const blob = await response.blob();
  const name = cleanUrlLabel(url) || fallbackName || "source.pdf";
  return new File([blob], name, { type: blob.type || fallbackType || "application/octet-stream" });
}

async function tryLoadBenchmarkManifestFromUrl(url) {
  try {
    loadBenchmarkManifestPayload(await fetchJson(url), url);
  } catch (error) {
    const message = error.message || String(error);
    setStatus("Benchmark failed");
    setBenchmarkDetails(null, [], message);
  }
}

async function tryLoadBenchmarkReviewSessionFromUrl(url) {
  try {
    loadBenchmarkReviewSessionPayload(await fetchJson(url), url);
  } catch (error) {
    const message = error.message || String(error);
    setStatus("Review session failed");
    setBenchmarkDetails(state.benchmarkManifest, activeBenchmarkTargets(), message);
  }
}

async function tryLoadBenchmarkResultFromUrl(url) {
  try {
    loadBenchmarkResultPayload(await fetchJson(url), url);
  } catch (error) {
    const message = error.message || String(error);
    setStatus("Benchmark result failed");
    setBenchmarkDetails(null, [], message);
  }
}

async function loadBenchmarkComparisonFromUrl(url) {
  resetViewerState("benchmarkComparison");
  elements.fileMeta.textContent = `Loading ${cleanUrlLabel(url)}`;
  setStatus("Loading comparison");

  try {
    loadBenchmarkComparisonPayload(await fetchJson(url), url);
  } catch (error) {
    setStatus("Failed");
    setEmptyStateMessage("Benchmark comparison failed", error.message || String(error));
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

function loadBenchmarkComparisonPayload(payload, label) {
  if (!isBenchmarkComparison(payload)) {
    throw new Error("JSON is not an OpenPlanTrace benchmark comparison.");
  }

  beginViewerOperation();
  beginDocumentLoad();
  resetViewerState("benchmarkComparison");
  state.benchmarkComparison = normalizeBenchmarkComparison(payload, label);
  state.selectedItem = null;

  setEmptyStateMessage(
    "Benchmark comparison loaded",
    "Open the General, Advanced, or Compare panels to inspect fixture regressions, scoring deltas, and signals.");
  elements.emptyState.style.display = "grid";
  elements.pageFrame.style.display = "none";
  elements.kvemoReview.hidden = true;
  elements.kvemoReview.replaceChildren();

  setCounts();
  setSourceLayers();
  setCalibration();
  setQuality();
  setTitleBlocks();
  setObjectGroups();
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(benchmarkComparisonDiagnostics(state.benchmarkComparison));
  setSelection();
  setLegend();
  setAnalysisCounts();
  updateNavigation();
  setWorkspaceTab("general");

  elements.fileMeta.textContent = `${state.benchmarkComparison.label} - ${state.benchmarkComparison.cases.length} case${state.benchmarkComparison.cases.length === 1 ? "" : "s"}`;
  setStatus(state.benchmarkComparison.passed ? "Benchmark comparison passed" : "Benchmark regression");
}

function isBenchmarkComparison(payload) {
  return payload?.schemaVersion === "openplantrace.benchmark-comparison.v1"
    || (Array.isArray(payload?.cases)
      && Array.isArray(payload?.signals)
      && payload?.baselineCaseCount != null
      && payload?.candidateCaseCount != null
      && payload?.matchedCaseCount != null);
}

function normalizeBenchmarkComparison(payload, label) {
  const cases = (Array.isArray(payload.cases) ? payload.cases : []).map(normalizeBenchmarkComparisonCase);
  const signals = (Array.isArray(payload.signals) ? payload.signals : [])
    .map(normalizeBenchmarkComparisonSignal)
    .filter(Boolean);
  return {
    schemaVersion: payload.schemaVersion || "openplantrace.benchmark-comparison.v1",
    label: cleanUrlLabel(label) || "Benchmark comparison",
    generatedAt: payload.generatedAt || "",
    baselineName: payload.baselineName || "",
    candidateName: payload.candidateName || "",
    baselineCaseCount: nonNegativeInteger(payload.baselineCaseCount),
    candidateCaseCount: nonNegativeInteger(payload.candidateCaseCount),
    matchedCaseCount: nonNegativeInteger(payload.matchedCaseCount),
    addedCaseCount: nonNegativeInteger(payload.addedCaseCount),
    removedCaseCount: nonNegativeInteger(payload.removedCaseCount),
    regressionCount: nonNegativeInteger(payload.regressionCount),
    improvementCount: nonNegativeInteger(payload.improvementCount),
    infoCount: signals.filter((signal) => signal.severity === "Info").length,
    passed: payload.passed == null ? nonNegativeInteger(payload.regressionCount) === 0 : Boolean(payload.passed),
    cases,
    signals,
    raw: payload
  };
}

function normalizeBenchmarkComparisonCase(item) {
  return {
    ...item,
    fixtureId: String(item?.fixtureId || item?.candidateName || item?.baselineName || "benchmark-case"),
    status: String(item?.status || "Matched"),
    countDeltas: Array.isArray(item?.countDeltas) ? item.countDeltas.map(normalizeBenchmarkCountDelta) : [],
    signals: Array.isArray(item?.signals)
      ? item.signals.map(normalizeBenchmarkComparisonSignal).filter(Boolean)
      : []
  };
}

function normalizeBenchmarkCountDelta(delta) {
  return {
    name: String(delta?.name || "metric"),
    baseline: nullableFiniteNumber(delta?.baseline),
    candidate: nullableFiniteNumber(delta?.candidate),
    delta: nullableFiniteNumber(delta?.delta)
  };
}

function normalizeBenchmarkComparisonSignal(signal) {
  if (!signal) {
    return null;
  }

  return {
    fixtureId: String(signal.fixtureId || signal.key || ""),
    code: String(signal.code || "benchmark.signal"),
    severity: benchmarkComparisonSeverity(signal.severity),
    message: String(signal.message || ""),
    baseline: signal.baseline == null ? "" : String(signal.baseline),
    candidate: signal.candidate == null ? "" : String(signal.candidate)
  };
}

function benchmarkComparisonSeverity(value) {
  switch (String(value || "").toLowerCase()) {
    case "regression":
      return "Regression";
    case "improvement":
      return "Improvement";
    default:
      return "Info";
  }
}

function benchmarkComparisonDiagnostics(comparison) {
  const signals = comparison?.signals ?? [];
  return {
    infoCount: signals.filter((signal) => signal.severity === "Info").length,
    warningCount: signals.filter((signal) => signal.severity === "Improvement").length,
    errorCount: signals.filter((signal) => signal.severity === "Regression").length,
    durationMilliseconds: 0,
    stages: ["benchmark-comparison"],
    messages: signals.slice(0, 40).map((signal) => ({
      severity: signal.severity === "Regression" ? "Error" : "Info",
      stage: "benchmark-comparison",
      scope: signal.fixtureId,
      code: signal.code,
      message: signal.message,
      properties: {
        baseline: signal.baseline || "-",
        candidate: signal.candidate || "-"
      }
    }))
  };
}

async function loadBatchComparisonFromUrl(url) {
  resetViewerState("batchComparison");
  elements.fileMeta.textContent = `Loading ${cleanUrlLabel(url)}`;
  setStatus("Loading comparison");

  try {
    loadBatchComparisonPayload(await fetchJson(url), url);
  } catch (error) {
    setStatus("Failed");
    setEmptyStateMessage("Batch comparison failed", error.message || String(error));
    elements.emptyState.style.display = "grid";
    elements.pageFrame.style.display = "none";
    setDiagnostics([{ severity: "Error", stage: "viewer", message: error.message || String(error) }]);
  }
}

function loadBatchComparisonPayload(payload, label) {
  if (!isBatchComparison(payload)) {
    throw new Error("JSON is not an OpenPlanTrace batch comparison.");
  }

  beginViewerOperation();
  beginDocumentLoad();
  resetViewerState("batchComparison");
  state.batchComparison = normalizeBatchComparison(payload, label);
  state.selectedItem = null;

  setEmptyStateMessage(
    "Batch comparison loaded",
    "Open the General, Advanced, or Compare panels to inspect regressions, improvements, and evidence paths.");
  elements.emptyState.style.display = "grid";
  elements.pageFrame.style.display = "none";
  elements.kvemoReview.hidden = true;
  elements.kvemoReview.replaceChildren();

  setCounts();
  setSourceLayers();
  setCalibration();
  setQuality();
  setTitleBlocks();
  setObjectGroups();
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(batchComparisonDiagnostics(state.batchComparison));
  setSelection();
  setLegend();
  setAnalysisCounts();
  updateNavigation();
  setWorkspaceTab("general");

  elements.fileMeta.textContent = `${state.batchComparison.label} - ${state.batchComparison.items.length} item${state.batchComparison.items.length === 1 ? "" : "s"}`;
  setStatus(state.batchComparison.passed ? "Comparison passed" : "Comparison regression");
}

function isBatchComparison(payload) {
  return payload?.schemaVersion === "openplantrace.batch-comparison.v1"
    || (Array.isArray(payload?.items)
      && Array.isArray(payload?.signals)
      && payload?.baselineItemCount != null
      && payload?.candidateItemCount != null
      && payload?.matchedItemCount != null);
}

function normalizeBatchComparison(payload, label) {
  const items = (Array.isArray(payload.items) ? payload.items : []).map(normalizeBatchComparisonItem);
  const signals = (Array.isArray(payload.signals) ? payload.signals : [])
    .map(normalizeBatchComparisonSignal)
    .filter(Boolean);
  return {
    schemaVersion: payload.schemaVersion || "openplantrace.batch-comparison.v1",
    label: cleanUrlLabel(label) || "Batch comparison",
    generatedAt: payload.generatedAt || "",
    baselineOutputDirectory: payload.baselineOutputDirectory || "",
    candidateOutputDirectory: payload.candidateOutputDirectory || "",
    baselineItemCount: nonNegativeInteger(payload.baselineItemCount),
    candidateItemCount: nonNegativeInteger(payload.candidateItemCount),
    matchedItemCount: nonNegativeInteger(payload.matchedItemCount),
    addedItemCount: nonNegativeInteger(payload.addedItemCount),
    removedItemCount: nonNegativeInteger(payload.removedItemCount),
    statusChangeCount: nonNegativeInteger(payload.statusChangeCount),
    regressionCount: nonNegativeInteger(payload.regressionCount),
    improvementCount: nonNegativeInteger(payload.improvementCount),
    infoCount: nonNegativeInteger(payload.infoCount),
    diagnosticErrorDelta: finiteNumber(payload.diagnosticErrorDelta, 0),
    diagnosticWarningDelta: finiteNumber(payload.diagnosticWarningDelta, 0),
    visualIssueDelta: finiteNumber(payload.visualIssueDelta, 0),
    visualErrorIssueDelta: finiteNumber(payload.visualErrorIssueDelta, 0),
    qualityConfidenceAverageDelta: finiteNumber(payload.qualityConfidenceAverageDelta, 0),
    totalDurationDeltaMilliseconds: finiteNumber(payload.totalDurationDeltaMilliseconds, 0),
    passed: payload.passed == null ? nonNegativeInteger(payload.regressionCount) === 0 : Boolean(payload.passed),
    items,
    signals,
    raw: payload
  };
}

function normalizeBatchComparisonItem(item) {
  return {
    ...item,
    key: String(item?.key || item?.candidateFileName || item?.baselineFileName || "comparison-item"),
    status: String(item?.status || "Matched"),
    deltas: Array.isArray(item?.deltas) ? item.deltas.map(normalizeBatchMetricDelta) : [],
    signals: Array.isArray(item?.signals)
      ? item.signals.map(normalizeBatchComparisonSignal).filter(Boolean)
      : [],
    addedVisualIssueCodes: normalizeStringArray(item?.addedVisualIssueCodes),
    removedVisualIssueCodes: normalizeStringArray(item?.removedVisualIssueCodes)
  };
}

function normalizeBatchMetricDelta(delta) {
  return {
    name: String(delta?.name || "metric"),
    baseline: nullableFiniteNumber(delta?.baseline),
    candidate: nullableFiniteNumber(delta?.candidate),
    delta: nullableFiniteNumber(delta?.delta),
    unit: String(delta?.unit || "count")
  };
}

function normalizeBatchComparisonSignal(signal) {
  if (!signal) {
    return null;
  }

  return {
    key: String(signal.key || ""),
    code: String(signal.code || "comparison.signal"),
    severity: batchComparisonSeverity(signal.severity),
    message: String(signal.message || ""),
    baseline: signal.baseline == null ? "" : String(signal.baseline),
    candidate: signal.candidate == null ? "" : String(signal.candidate)
  };
}

function batchComparisonSeverity(value) {
  switch (String(value || "").toLowerCase()) {
    case "regression":
      return "Regression";
    case "improvement":
      return "Improvement";
    default:
      return "Info";
  }
}

function batchComparisonDiagnostics(comparison) {
  const signals = comparison?.signals ?? [];
  return {
    infoCount: signals.filter((signal) => signal.severity === "Info").length,
    warningCount: signals.filter((signal) => signal.severity === "Improvement").length,
    errorCount: signals.filter((signal) => signal.severity === "Regression").length,
    durationMilliseconds: comparison?.totalDurationDeltaMilliseconds ?? 0,
    stages: ["batch-comparison"],
    messages: signals.slice(0, 40).map((signal) => ({
      severity: signal.severity === "Regression" ? "Error" : signal.severity === "Improvement" ? "Info" : "Info",
      stage: "batch-comparison",
      scope: signal.key,
      code: signal.code,
      message: signal.message,
      properties: {
        baseline: signal.baseline || "-",
        candidate: signal.candidate || "-"
      }
    }))
  };
}

function nonNegativeInteger(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? Math.floor(number) : 0;
}

function finiteNumber(value, fallback = 0) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function nullableFiniteNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

async function loadPdfForPreview(file) {
  const bytes = await file.arrayBuffer();
  return pdfjsLib.getDocument({ data: bytes }).promise;
}

async function parseJsonFile(file) {
  try {
    return JSON.parse(await file.text());
  } catch (error) {
    throw new Error(`${file.name} is not valid JSON: ${error.message || String(error)}`);
  }
}

async function scanPdf(
  file,
  documentRevision = state.documentLoadRevision,
  overlayRevision = state.benchmarkOverlayRevision,
  operationRevision = state.viewerOperationRevision) {
  setStatus("Scanning");
  const form = new FormData();
  form.append("file", file, file.name);

  const response = await fetch("/api/scan", {
    method: "POST",
    body: form
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || problem.error || "Scan failed");
  }

  if (!isCurrentDocumentLoad(documentRevision)) {
    return;
  }

  state.placement = null;
  state.scan = normalizeScanPayload(await response.json());
  state.enabledSourceLayers = new Set((state.scan.layers ?? []).map((layer) => layerKey(layer.name)));
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    state.benchmarkResult = null;
    state.benchmarkManifest = null;
    state.benchmarkTargets = [];
    state.benchmarkReviewDecisions = new Map();
    state.benchmarkTargetEdits = new Map();
    state.benchmarkDeletedTargets = new Set();
    state.benchmarkAddedTargetSequence = 1;
    state.benchmarkFilters = resetBenchmarkFilters();
    state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
    state.benchmarkDrawBox = null;
    state.benchmarkSuppressNextOverlayClick = false;
  }
  setCounts(state.scan);
  setSourceLayers(state.scan);
  setCalibration(state.scan.calibration, state.scan.measurementConsistency);
  setQuality(state.scan.quality);
  setTitleBlocks(state.scan);
  setObjectGroups(state.scan);
  setCompare();
  setBenchmarkDetails();
  setDiagnostics(state.scan.diagnostics);
  setSelection(
    benchmarkOverlayChangedSince(overlayRevision) || viewerOperationChangedSince(operationRevision)
      ? state.selectedItem
      : null);
  if (!benchmarkOverlayChangedSince(overlayRevision) && !viewerOperationChangedSince(operationRevision)) {
    setStatus("Ready");
  }
}

async function renderCurrentPage(options = {}) {
  const preserveStatus = Boolean(options.preserveStatus);
  if (state.rendering) {
    return;
  }

  if (!state.pdf) {
    renderScanJsonCurrentPage(options);
    return;
  }

  state.rendering = true;
  if (!preserveStatus) {
    setStatus("Rendering");
  }

  const page = await state.pdf.getPage(state.currentPage);
  const baseViewport = page.getViewport({ scale: 1 });
  const availableWidth = Math.max(320, elements.stage.clientWidth - 48);
  const scale = Math.min(2.0, availableWidth / baseViewport.width);
  const viewport = page.getViewport({ scale });
  const pixelRatio = window.devicePixelRatio || 1;

  elements.canvas.width = Math.floor(viewport.width * pixelRatio);
  elements.canvas.height = Math.floor(viewport.height * pixelRatio);
  elements.canvas.style.width = `${viewport.width}px`;
  elements.canvas.style.height = `${viewport.height}px`;
  elements.overlay.style.width = `${viewport.width}px`;
  elements.overlay.style.height = `${viewport.height}px`;
  elements.pageFrame.style.width = `${viewport.width}px`;
  elements.pageFrame.style.height = `${viewport.height}px`;

  context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
  await page.render({ canvasContext: context, viewport }).promise;
  setSourceUnderlayBadge();

  drawOverlay();
  updateNavigation();
  setTitleBlocks(state.scan);
  if (!preserveStatus) {
    setStatus("Ready");
  }
  state.rendering = false;
}

function renderScanJsonCurrentPage(options = {}) {
  const preserveStatus = Boolean(options.preserveStatus);
  const page = currentPageDefinition();
  if (!page) {
    return;
  }

  state.rendering = true;
  if (!preserveStatus) {
    setStatus("Rendering");
  }

  const availableWidth = Math.max(320, elements.stage.clientWidth - 48);
  const scale = Math.min(2.0, availableWidth / page.width);
  const viewportWidth = Math.max(1, page.width * scale);
  const viewportHeight = Math.max(1, page.height * scale);
  const pixelRatio = window.devicePixelRatio || 1;

  elements.canvas.width = Math.floor(viewportWidth * pixelRatio);
  elements.canvas.height = Math.floor(viewportHeight * pixelRatio);
  elements.canvas.style.width = `${viewportWidth}px`;
  elements.canvas.style.height = `${viewportHeight}px`;
  elements.overlay.style.width = `${viewportWidth}px`;
  elements.overlay.style.height = `${viewportHeight}px`;
  elements.pageFrame.style.width = `${viewportWidth}px`;
  elements.pageFrame.style.height = `${viewportHeight}px`;

  context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
  context.clearRect(0, 0, viewportWidth, viewportHeight);
  context.fillStyle = "#ffffff";
  context.fillRect(0, 0, viewportWidth, viewportHeight);
  drawBlankPageGrid(viewportWidth, viewportHeight, scale);
  setSourceUnderlayBadge();

  drawOverlay();
  updateNavigation();
  setTitleBlocks(state.scan);
  if (!preserveStatus) {
    setStatus("Ready");
  }
  state.rendering = false;
}

function drawBlankPageGrid(width, height, scale) {
  const gridSize = Math.max(12, 50 * scale);
  context.save();
  context.strokeStyle = "#eceff3";
  context.lineWidth = 1;
  for (let x = 0; x <= width; x += gridSize) {
    context.beginPath();
    context.moveTo(x, 0);
    context.lineTo(x, height);
    context.stroke();
  }
  for (let y = 0; y <= height; y += gridSize) {
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(width, y);
    context.stroke();
  }
  context.restore();
}

function setSourceUnderlayBadge() {
  if (!elements.sourceUnderlayBadge) {
    return;
  }

  const status = sourceUnderlayStatus();
  elements.sourceUnderlayBadge.textContent = status.label;
  elements.sourceUnderlayBadge.hidden = !status.visible;
  elements.sourceUnderlayBadge.classList.toggle("source-underlay-missing", status.kind === "missing");
  elements.sourceUnderlayBadge.classList.toggle("source-underlay-ready", status.kind === "ready");
}

function sourceUnderlayStatus() {
  if (!state.scan && !state.visualSnapshot) {
    return { visible: false, kind: "none", label: "" };
  }

  if (state.pdf) {
    return { visible: true, kind: "ready", label: "PDF source underlay" };
  }

  return { visible: true, kind: "missing", label: "Overlay-only: source PDF not loaded" };
}

function drawVisualSnapshotOverlay() {
  const page = visualSnapshotCurrentPage();
  if (!page) {
    return;
  }

  elements.overlay.setAttribute("viewBox", `0 0 ${page.width} ${page.height}`);
  setCoordinateDetails();
  setLegend();
  setAnalysisCounts();

  (page.layers ?? [])
    .filter((layer) => layer.count > 0 && normalizeRect(layer.bounds))
    .forEach((layer) => {
      const key = visualSnapshotOverlayKey(layer.name);
      if (key && !state.enabledLayers.has(key)) {
        return;
      }

      const item = {
        type: "Snapshot layer",
        id: layer.name,
        kind: snapshotLayerLabel(layer.name),
        pageNumber: page.pageNumber,
        bounds: layer.bounds,
        confidence: layer.averageConfidence,
        metadata: visualSnapshotLayerMeta(layer)
      };
      addRect(
        layer.bounds,
        `visual-snapshot-layer ${key || "snapshot"}`,
        `${snapshotLayerLabel(layer.name)} - ${layer.count} item${layer.count === 1 ? "" : "s"}, density ${formatNumber(layer.normalizedDensity ?? 0, 1)}`,
        Math.max(0.18, Math.min(0.72, 0.22 + Math.min(0.5, (layer.normalizedDensity ?? 0) / 4000))),
        item);
    });
}

function drawOverlay() {
  clearOverlay();

  if (!state.scan) {
    if (state.visualSnapshot) {
      drawVisualSnapshotOverlay();
    }
    return;
  }

  const page = currentPageDefinition();
  if (!page) {
    return;
  }

  elements.overlay.setAttribute("viewBox", `0 0 ${page.width} ${page.height}`);
  setCoordinateDetails(state.scan);
  setLegend();
  setAnalysisCounts(state.scan);

  if (state.enabledLayers.has("regions")) {
    state.scan.regions.filter(onCurrentPage).forEach((region) => {
      if (!visibleBySourceLayer(region)) {
        return;
      }
      const className = `region ${String(region.kind).toLowerCase()}`;
      const titleBlock = String(region.kind).toLowerCase() === "titleblock"
        ? findTitleBlockForRegion(region)
        : null;
      addRect(
        region.bounds,
        className,
        region.label || region.kind,
        confidence(region.confidence),
        titleBlock ? describeTitleBlock(titleBlock) : describeItem("region", region));
    });
  }

  if (state.enabledLayers.has("rooms")) {
    state.scan.rooms.filter(onCurrentPage).forEach((room) => {
      if (room.boundary?.length >= 3) {
        addPolygon(room.boundary, "room", room.label || room.id, confidence(room.confidence), describeItem("room", room));
      } else {
        addRect(room.bounds, "room", room.label || room.id, confidence(room.confidence), describeItem("room", room));
      }
    });
  }

  if (state.enabledLayers.has("roomClusters")) {
    state.scan.roomClusters.filter(onCurrentPage).forEach((cluster) => {
      addRect(
        cluster.bounds,
        "room-cluster",
        `${cluster.kind ? `${cluster.kind}: ` : ""}${cluster.roomLabels?.join(" + ") || cluster.id}`,
        confidence(cluster.confidence),
        describeRoomCluster(cluster));
    });
  }

  if (state.enabledLayers.has("roomAdjacency")) {
    const roomsById = new Map(state.scan.rooms.filter(onCurrentPage).map((room) => [room.id, room]));
    state.scan.roomAdjacencyEdges.filter(onCurrentPage).forEach((edge) => {
      const first = roomsById.get(edge.firstRoomId);
      const second = roomsById.get(edge.secondRoomId);
      if (!first || !second) {
        return;
      }

      const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
      line.setAttribute("x1", rectCenter(first.bounds).x);
      line.setAttribute("y1", rectCenter(first.bounds).y);
      line.setAttribute("x2", rectCenter(second.bounds).x);
      line.setAttribute("y2", rectCenter(second.bounds).y);
      line.setAttribute("class", "room-adjacency");
      line.setAttribute("opacity", confidence(edge.confidence));
      addTitle(line, `${edge.kind} - ${edge.firstRoomLabel || edge.firstRoomId} to ${edge.secondRoomLabel || edge.secondRoomId}`);
      attachInspection(line, describeRoomAdjacency(edge));
      elements.overlay.appendChild(line);
    });
  }

  if (state.enabledLayers.has("dimensions")) {
    state.scan.dimensions.filter(onCurrentPage).forEach((dimension) => {
      if (!visibleBySourceLayer(dimension)) {
        return;
      }

      addRect(
        dimension.bounds,
        "dimension",
        `${dimension.normalizedText || dimension.text} - ${dimension.id}`,
        confidence(dimension.confidence),
        describeDimension(dimension));

      if (dimension.dimensionLine) {
        const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
        line.setAttribute("x1", dimension.dimensionLine.start.x);
        line.setAttribute("y1", dimension.dimensionLine.start.y);
        line.setAttribute("x2", dimension.dimensionLine.end.x);
        line.setAttribute("y2", dimension.dimensionLine.end.y);
        line.setAttribute("class", "dimension-line");
        line.setAttribute("opacity", confidence(dimension.confidence));
        addTitle(line, `${dimension.normalizedText || dimension.text} - confidence ${Number(dimension.confidence ?? 0).toFixed(2)}`);
        attachInspection(line, describeDimension(dimension));
        elements.overlay.appendChild(line);
      }
    });
  }

  if (state.enabledLayers.has("annotations")) {
    state.scan.annotations.filter(onCurrentPage).forEach((annotation) => {
      if (!visibleBySourceLayer(annotation)) {
        return;
      }

      addRect(
        annotation.bounds,
        "annotation",
        `${annotation.kind} - ${annotation.label || annotation.id}`,
        confidence(annotation.confidence),
        describeAnnotation(annotation));

      drawAnnotationReferences(annotation);
    });
  }

  if (state.enabledLayers.has("gridAxes")) {
    state.scan.gridAxes.filter(onCurrentPage).forEach((axis) => {
      if (!visibleBySourceLayer(axis)) {
        return;
      }

      const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
      line.setAttribute("x1", axis.line.start.x);
      line.setAttribute("y1", axis.line.start.y);
      line.setAttribute("x2", axis.line.end.x);
      line.setAttribute("y2", axis.line.end.y);
      line.setAttribute("class", "grid-axis");
      line.setAttribute("opacity", confidence(axis.confidence));
      addTitle(line, `${axis.label || axis.id} - ${axis.orientation}`);
      attachInspection(line, describeGridAxis(axis));
      elements.overlay.appendChild(line);

      if (axis.label) {
        const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
        const point = gridLabelPoint(axis);
        label.setAttribute("x", point.x);
        label.setAttribute("y", point.y);
        label.setAttribute("class", "grid-label");
        label.setAttribute("text-anchor", "middle");
        label.textContent = axis.label;
        elements.overlay.appendChild(label);
      }
    });
  }

  if (state.enabledLayers.has("gridBays")) {
    state.scan.gridBaySpacings.filter(onCurrentPage).forEach((bay) => {
      if (!visibleBySourceLayer(bay)) {
        return;
      }

      const label = `${bay.firstAxisLabel || bay.firstAxisId} -> ${bay.secondAxisLabel || bay.secondAxisId}`;
      const measurement = bay.distanceMeters == null
        ? `${formatNumber(bay.drawingDistance)} drawing units`
        : `${formatNumber(bay.distanceMeters)} m`;
      addLine(
        bay.line,
        "grid-bay",
        `${label} - ${measurement}`,
        confidence(bay.confidence),
        describeGridBaySpacing(bay));
    });
  }

  if (state.enabledLayers.has("openings")) {
    state.scan.openings.filter(onCurrentPage).forEach((opening) => {
      if (!visibleBySourceLayer(opening)) {
        return;
      }
      addRect(opening.bounds, "opening", `${opening.type} - ${opening.id}`, confidence(opening.confidence), describeItem("opening", opening));
      if (opening.centerLine) {
        addLine(opening.centerLine, "opening-line", `${opening.type} placement line - ${opening.id}`, confidence(opening.confidence), describeItem("opening", opening));
      }
    });
  }

  if (state.enabledLayers.has("objects")) {
    state.scan.objects.filter(onCurrentPage).forEach((object) => {
      if (!visibleBySourceLayer(object)) {
        return;
      }
      addRect(object.bounds, "object", visualAiLabel(object) || object.label || object.category || object.kind, confidence(object.confidence), describeItem("object", object));
    });
  }

  if (state.enabledLayers.has("objectAggregates")) {
    state.scan.objectAggregates.filter(onCurrentPage).forEach((aggregate) => {
      if (!visibleBySourceLayer(aggregate)) {
        return;
      }

      const title = [
        aggregate.label || aggregate.category || aggregate.kind || aggregate.id,
        `${aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0} child objects`,
        aggregate.routingInfluence ? `routing ${aggregate.routingInfluence}` : ""
      ].filter(Boolean).join(" - ");
      addRect(
        aggregate.bounds,
        "object-aggregate",
        title,
        confidence(aggregate.confidence),
        describeObjectAggregate(aggregate));
    });
  }

  if (state.enabledLayers.has("surfacePatterns")) {
    state.scan.surfacePatterns.filter(onCurrentPage).forEach((pattern) => {
      if (!visibleBySourceLayer(pattern)) {
        return;
      }

      const spacing = pattern.medianSpacing ?? pattern.horizontalMedianSpacing ?? pattern.verticalMedianSpacing;
      const title = [
        pattern.kind || "Surface pattern",
        pattern.orientation || "",
        `${pattern.lineCount ?? pattern.sourcePrimitiveIds?.length ?? 0} lines`,
        Number.isFinite(Number(spacing)) ? `${formatNumber(spacing)} spacing` : ""
      ].filter(Boolean).join(" - ");
      addRect(
        pattern.bounds,
        "surface-pattern",
        title,
        confidence(pattern.confidence),
        describeSurfacePattern(pattern));
      addSurfacePatternLabel(pattern, title);
    });
  }

  if (state.enabledLayers.has("wallComponents")) {
    state.scan.wallComponents.filter(onCurrentPage).forEach((component) => {
      if (!visibleBySourceLayer(component)) {
        return;
      }

      const cssClass = component.kind === "ObjectLikeIsland"
        ? "wall-component wall-component-object"
        : component.kind === "IsolatedFragment"
          ? "wall-component wall-component-fragment"
          : "wall-component";
      const wallCount = component.wallCount ?? component.wallIds?.length ?? 0;
      addRect(
        component.bounds,
        cssClass,
        `${component.kind} - ${wallCount} walls`,
        confidence(component.confidence),
        describeItem("wall component", component));
    });
  }

  if (state.enabledLayers.has("wallBodyFootprints")) {
    state.scan.walls.filter(onCurrentPage).forEach((wall) => {
      if (!visibleBySourceLayer(wall)) {
        return;
      }
      if (!shouldDrawWallAsCleanTopologySpan(wall)) {
        return;
      }

      wallBodyFootprints(wall).forEach((footprint) => {
        if (!visibleBySourceLayer(footprint)) {
          return;
        }

        const title = [
          `${footprint.id} - wall body for ${wall.id}`,
          wall.wallType ? `type ${wall.wallType}` : "",
          footprint.geometrySource ? `body from ${footprint.geometrySource}` : "",
          Number.isFinite(Number(footprint.thickness)) ? `${formatNumber(footprint.thickness)} units thick` : "",
          wallReliabilitySummary(wall)
        ].filter(Boolean).join(" - ");
        const inspection = describeItem("wall body footprint", {
          ...footprint,
          sourceWallId: wall.id,
          sourceWallType: wall.wallType,
          sourceWallComponentKind: wall.wallComponentKind,
          sourceWallReliability: wall.reliability ?? null,
          sourceWallEvidenceAssessment: wall.evidenceAssessment ?? null
        });
        addPolygon(
          footprint.bodyPolygon,
          wallBodyFootprintClassName(wall),
          title,
          confidence(footprint.confidence ?? wall.confidence),
          inspection);
      });
    });
  }

  if (state.enabledLayers.has("wallTopologySpans")) {
    state.scan.walls.filter(onCurrentPage).forEach((wall) => {
      if (!visibleBySourceLayer(wall)) {
        return;
      }
      if (!shouldDrawWallAsCleanTopologySpan(wall)) {
        return;
      }

      wallCleanTopologySpans(wall).forEach((span) => {
        if (!visibleBySourceLayer(span)) {
          return;
        }

        const className = wallTopologySpanClassName(wall);
        const title = [
          `${span.id} - clean span for ${wall.id}`,
          wall.wallType ? `type ${wall.wallType}` : "",
          span.wallGraphEdgeId ? `edge ${span.wallGraphEdgeId}` : "",
          Number.isFinite(Number(span.drawingLength)) ? `${formatNumber(span.drawingLength)} units` : "",
          wallReliabilitySummary(wall)
        ].filter(Boolean).join(" - ");
        const inspection = describeItem("clean wall topology span", {
          ...span,
          sourceWallId: wall.id,
          sourceWallType: wall.wallType,
          sourceWallComponentKind: wall.wallComponentKind,
          sourceWallReliability: wall.reliability ?? null,
          sourceWallEvidenceAssessment: wall.evidenceAssessment ?? null
        });
        addLine(span.centerLine, `${className} wall-topology-span-halo`, "", 1);
        addLine(span.centerLine, className, title, confidence(span.confidence ?? wall.confidence), inspection);
      });
    });
  }

  if (state.enabledLayers.has("wallTopologyReviewSpans")) {
    state.scan.walls.filter(onCurrentPage).forEach((wall) => {
      if (!visibleBySourceLayer(wall)) {
        return;
      }
      if (!shouldDrawWallAsReviewTopologySpan(wall)) {
        return;
      }

      wallCleanTopologySpans(wall).forEach((span) => {
        if (!visibleBySourceLayer(span)) {
          return;
        }

        const className = `${wallTopologySpanClassName(wall)} wall-topology-span-review-only`;
        const title = [
          `${span.id} - review span for ${wall.id}`,
          wall.wallType ? `type ${wall.wallType}` : "",
          span.wallGraphEdgeId ? `edge ${span.wallGraphEdgeId}` : "",
          Number.isFinite(Number(span.drawingLength)) ? `${formatNumber(span.drawingLength)} units` : "",
          wallReliabilitySummary(wall)
        ].filter(Boolean).join(" - ");
        const inspection = describeItem("review wall topology span", {
          ...span,
          sourceWallId: wall.id,
          sourceWallType: wall.wallType,
          sourceWallComponentKind: wall.wallComponentKind,
          sourceWallReliability: wall.reliability ?? null,
          sourceWallEvidenceAssessment: wall.evidenceAssessment ?? null
        });
        addLine(span.centerLine, `${className} wall-topology-span-halo`, "", 1);
        addLine(span.centerLine, className, title, confidence(span.confidence ?? wall.confidence), inspection);
      });
    });
  }

  if (state.enabledLayers.has("walls")) {
    state.scan.walls.filter(onCurrentPage).forEach((wall) => {
      if (!visibleBySourceLayer(wall)) {
        return;
      }
      if (!shouldDrawWallInStructuralLayer(wall)) {
        return;
      }
      const component = wall.wallComponentKind
        ? `${wall.wallComponentKind}${wall.excludedFromStructuralTopology ? ", topology excluded" : ""}`
        : "no component";
      const inspection = describeItem("wall", wall);
      const reliability = wallReliabilitySummary(wall);
      const title = [
        `${wall.id} - ${component}`,
        wall.wallType ? `type ${wall.wallType}` : "",
        wall.evidenceAssessment?.category ? `evidence ${wall.evidenceAssessment.category}` : "",
        `confidence ${wall.confidence.toFixed(2)}`,
        reliability
      ].filter(Boolean).join(" - ");
      wallCleanTopologySpans(wall).forEach((span) => {
        const className = wallClassName(wall);
        const opacity = wallDrawOpacity({ ...wall, confidence: span.confidence ?? wall.confidence });
        addLine(span.centerLine, `${className} wall-halo`, "", 1);
        addLine(span.centerLine, className, title, opacity, inspection);
      });
      addWallHitTarget(wall, title, inspection);
    });
  }

  if (state.enabledLayers.has("nodes")) {
    state.scan.nodes.filter(onCurrentPage).forEach((node) => {
      const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
      circle.setAttribute("cx", node.position.x);
      circle.setAttribute("cy", node.position.y);
      circle.setAttribute("r", 0.95);
      circle.setAttribute("class", "node");
      circle.setAttribute("opacity", nodeOpacity(node.confidence));
      addTitle(circle, `${node.kind} - ${node.id}`);
      attachInspection(circle, describeItem("node", node));
      elements.overlay.appendChild(circle);
    });
  }

  if (state.enabledLayers.has("wallGraphRepairs")) {
    state.scan.wallGraphRepairCandidates.filter(onCurrentPage).forEach((candidate) => {
      if (!visibleBySourceLayer(candidate)) {
        return;
      }

      const gapDistance = candidate.gapDistanceDrawingUnits ?? candidate.gapDistance;
      const title = [
        candidate.suggestedAction || candidate.kind || "Wall graph repair",
        candidate.importImpact || "",
        candidate.severity ? `${String(candidate.severity).toLowerCase()} severity` : "",
        Number.isFinite(Number(gapDistance)) ? `${formatNumber(gapDistance)} drawing units` : "",
        candidate.id
      ].filter(Boolean).join(" - ");
      const inspection = describeWallGraphRepairCandidate(candidate);
      addLine(candidate.repairLine, "wall-graph-repair", title, confidence(candidate.confidence), inspection);
      addCircle(candidate.sourcePoint, 1.6, "wall-graph-repair-point", `Repair source - ${candidate.sourceNodeId}`, confidence(candidate.confidence), inspection);
      addCircle(candidate.targetPoint, 1.6, "wall-graph-repair-point", `Repair target - ${candidate.targetNodeId || candidate.hostWallId || candidate.id}`, confidence(candidate.confidence), inspection);
    });
  }

  if (state.enabledLayers.has("routingLayer")) {
    drawRoutingLayer();
  }

  if (state.enabledLayers.has("suppressedDetails")) {
    drawScanReviewQueue(page, {
      includeKindKeys: new Set(["suppressed-wall-pattern-review"])
    });
  }

  if (state.enabledLayers.has("wallGraphGaps")) {
    drawScanReviewQueue(page, {
      includeKindKeys: new Set(["wall-graph-gap-review"])
    });
  }

  if (state.enabledLayers.has("reviewQueue")) {
    const excludedReviewKinds = new Set();
    if (state.enabledLayers.has("suppressedDetails")) {
      excludedReviewKinds.add("suppressed-wall-pattern-review");
    }
    if (state.enabledLayers.has("wallGraphGaps")) {
      excludedReviewKinds.add("wall-graph-gap-review");
    }

    drawScanReviewQueue(page, {
      excludeKindKeys: excludedReviewKinds.size ? excludedReviewKinds : null
    });
  }

  if (state.enabledLayers.has("placementIssues")) {
    drawPlacementIssues(page);
  }

  drawCompareOverlay(page);
  drawBenchmarkTargets(page);
  drawManualBenchmarkTargetDraft(page);
}

function shouldDrawWallInStructuralLayer(wall) {
  return shouldDrawWallInStructuralCandidateLayer(wall) && !wallCoordinateBlocked(wall);
}

function shouldDrawWallInStructuralCandidateLayer(wall) {
  if (wall?.excludedFromStructuralTopology) {
    return false;
  }

  if (wall?.wallComponentKind === "ObjectLikeIsland" || wall?.wallComponentKind === "IsolatedFragment") {
    return false;
  }

  return true;
}

function wallIsPlacementReady(wall) {
  if (wall?.readyForCoordinatePlacement === false || wall?.requiresReview === true) {
    return false;
  }

  const assessment = wall?.evidenceAssessment;
  if (!assessment) {
    return true;
  }

  return assessment.placementReady !== false && assessment.requiresReview !== true;
}

function shouldDrawWallAsCleanTopologySpan(wall) {
  return shouldDrawWallInStructuralLayer(wall) && wallIsPlacementReady(wall);
}

function shouldDrawWallAsPlacementWall(wall) {
  return shouldDrawWallAsCleanTopologySpan(wall) && wallCleanTopologySpans(wall).length > 0;
}

function shouldDrawWallAsReviewTopologySpan(wall) {
  return shouldDrawWallInStructuralCandidateLayer(wall) && !wallIsPlacementReady(wall);
}

function drawScanReviewQueue(page, options = {}) {
  const labelSlots = new Map();
  scanReviewQueueForPage(state.scan, page.number)
    .filter((item) => {
      if (!item.bounds || !visibleBySourceLayer(item)) {
        return false;
      }

      const kind = scanReviewQueueKindKey(item);
      if (options.includeKindKeys && !options.includeKindKeys.has(kind)) {
        return false;
      }

      return !(options.excludeKindKeys && options.excludeKindKeys.has(kind));
    })
    .forEach((item) => {
      const selected = describeScanReviewQueueItem(item);
      addRect(
        item.bounds,
        scanReviewQueueClassName(item),
        `${scanReviewQueueKindLabel(item.kind)} - ${item.itemId || item.id}`,
        scanReviewQueueOpacity(item),
        selected);
      addScanReviewQueueLabel(item, selected, labelSlots);
    });
}

function drawPlacementIssues(page) {
  placementIssuesForPage(page.number)
    .filter((issue) => issue.bounds)
    .sort(comparePlacementIssuePaintOrder)
    .forEach((issue) => {
      const selected = describePlacementIssue(issue);
      addRect(
        issue.bounds,
        `placement-issue placement-issue-${String(issue.severity || "info").toLowerCase()}`,
        `${issue.code || "placement.issue"} - ${issue.message || issue.itemId || issue.id}`,
        placementIssueOpacity(issue),
        selected);
      addPlacementIssueLabel(issue, selected);
    });
}

function addPlacementIssueLabel(issue, selected) {
  const labelText = placementIssueShortLabel(issue);
  if (!labelText || !selected?.bounds || selected.bounds.width < 18 || selected.bounds.height < 10) {
    return;
  }

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  const center = rectCenter(selected.bounds);
  label.setAttribute("x", center.x);
  label.setAttribute("y", center.y);
  label.setAttribute("class", "placement-issue-label");
  label.setAttribute("text-anchor", "middle");
  label.setAttribute("dominant-baseline", "middle");
  label.textContent = labelText;
  addTitle(label, `${issue.code || "placement.issue"} - ${issue.itemId || issue.id || ""}`.trim());
  elements.overlay.appendChild(label);
}

function drawRoutingLayer() {
  const routing = state.scan?.routingLayer;
  if (!routing) {
    return;
  }

  (routing.barriers ?? []).filter(onCurrentPage).forEach((barrier) => {
    if (!visibleBySourceLayer(barrier)) {
      return;
    }
    addLine(
      barrier.centerLine,
      "routing-barrier",
      `${barrier.sourceId} - structural routing barrier`,
      confidence(barrier.confidence),
      describeRoutingItem("routing barrier", barrier));
  });

  (routing.passages ?? []).filter(onCurrentPage).forEach((passage) => {
    if (!visibleBySourceLayer(passage)) {
      return;
    }
    addLine(
      passage.centerLine,
      "routing-passage",
      `${passage.type} - ${passage.operation} - ${formatNumber(passage.drawingWidth)} drawing units`,
      confidence(passage.confidence),
      describeRoutingItem("routing passage", passage));
  });

  (routing.obstacles ?? []).filter(onCurrentPage).forEach((obstacle) => {
    if (!visibleBySourceLayer(obstacle)) {
      return;
    }
    const cssClass = obstacle.obstacleKind === "StructuralBarrier"
      ? "routing-obstacle routing-obstacle-structural"
      : obstacle.obstacleKind === "HardObstacle"
        ? "routing-obstacle routing-obstacle-hard"
        : "routing-obstacle";
    addRect(
      obstacle.bounds,
      cssClass,
      `${obstacle.label || obstacle.category || obstacle.objectKind || obstacle.sourceId} - ${obstacle.routingInfluence}`,
      confidence(obstacle.confidence),
      describeRoutingItem("routing obstacle", obstacle));
  });

  (routing.roomUseHints ?? []).filter(onCurrentPage).forEach((hint) => {
    if (!visibleBySourceLayer(hint)) {
      return;
    }
    addRect(
      hint.bounds,
      "routing-room-use",
      `${hint.roomUseKind} room-use hint - ${hint.sourceKind}`,
      confidence(hint.confidence),
      describeRoutingItem("routing room-use hint", hint));
  });

  (routing.suppressedObjects ?? []).filter(onCurrentPage).forEach((suppressed) => {
    if (!visibleBySourceLayer(suppressed)) {
      return;
    }
    addRect(
      suppressed.candidateBounds,
      "routing-suppressed-object",
      `${suppressed.objectCandidateId} - ${suppressed.action}`,
      confidence(suppressed.confidence),
      describeRoutingItem("routing suppressed object", suppressed));
  });

  (routing.ignoredObjects ?? []).filter(onCurrentPage).forEach((ignored) => {
    if (!visibleBySourceLayer(ignored)) {
      return;
    }
    addRect(
      ignored.candidateBounds,
      "routing-ignored-object",
      `${ignored.candidateLabel || ignored.objectCandidateId} - ${ignored.reason}`,
      confidence(ignored.confidence),
      describeRoutingItem("routing ignored object", ignored));
  });
}

function addRect(bounds, className, title, opacity, item = null) {
  if (!bounds) {
    return;
  }

  const rect = document.createElementNS("http://www.w3.org/2000/svg", "rect");
  rect.setAttribute("x", bounds.x);
  rect.setAttribute("y", bounds.y);
  rect.setAttribute("width", Math.max(0, bounds.width));
  rect.setAttribute("height", Math.max(0, bounds.height));
  rect.setAttribute("class", className);
  rect.setAttribute("opacity", opacity);
  addTitle(rect, title);
  attachInspection(rect, item);
  elements.overlay.appendChild(rect);
}

function addPolygon(points, className, title, opacity, item = null) {
  if (!points?.length) {
    return;
  }

  const polygon = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
  polygon.setAttribute("points", points.map((point) => `${point.x},${point.y}`).join(" "));
  polygon.setAttribute("class", className);
  polygon.setAttribute("opacity", opacity);
  addTitle(polygon, title);
  attachInspection(polygon, item);
  elements.overlay.appendChild(polygon);
}

function addLine(line, className, title, opacity, item = null) {
  if (!line?.start || !line?.end) {
    return;
  }

  const element = document.createElementNS("http://www.w3.org/2000/svg", "line");
  element.setAttribute("x1", line.start.x);
  element.setAttribute("y1", line.start.y);
  element.setAttribute("x2", line.end.x);
  element.setAttribute("y2", line.end.y);
  element.setAttribute("class", className);
  element.setAttribute("opacity", opacity);
  addTitle(element, title);
  attachInspection(element, item);
  elements.overlay.appendChild(element);
}

function addWallHitTarget(wall, title, item) {
  const spans = wallCleanTopologySpans(wall);
  if (!spans.length) {
    return;
  }

  spans.forEach((span) => {
    const element = document.createElementNS("http://www.w3.org/2000/svg", "line");
    element.setAttribute("x1", span.centerLine.start.x);
    element.setAttribute("y1", span.centerLine.start.y);
    element.setAttribute("x2", span.centerLine.end.x);
    element.setAttribute("y2", span.centerLine.end.y);
    element.setAttribute("class", [
      "wall-hit-target",
      wallRequiresReliabilityReview(wall) ? "wall-review-hit-target" : "",
      wallCoordinateBlocked(wall) ? "wall-blocked-hit-target" : ""
    ].filter(Boolean).join(" "));
    addTitle(element, title);
    attachInspection(element, item);
    elements.overlay.appendChild(element);
  });
}

function addCircle(point, radius, className, title, opacity, item = null) {
  if (!point) {
    return;
  }

  const circle = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  circle.setAttribute("cx", point.x);
  circle.setAttribute("cy", point.y);
  circle.setAttribute("r", radius);
  circle.setAttribute("class", className);
  circle.setAttribute("opacity", opacity);
  addTitle(circle, title);
  attachInspection(circle, item);
  elements.overlay.appendChild(circle);
}

function drawAnnotationReferences(annotation) {
  (annotation.items ?? []).forEach((item) => {
    if ((item.pageNumber ?? annotation.pageNumber) !== state.currentPage) {
      return;
    }

    (item.references ?? []).forEach((reference) => {
      if (!visibleBySourceLayer(reference)) {
        return;
      }

      const inspection = describeAnnotationReference(annotation, item, reference);
      const itemCenter = rectCenter(item.bounds);
      const referenceCenter = rectCenter(reference.bounds);
      const marker = reference.marker || item.marker || reference.id;
      const title = `${annotation.kind || "Annotation"} reference ${marker}`;

      addLine(
        { start: itemCenter, end: referenceCenter },
        "annotation-reference-link",
        title,
        confidence(reference.confidence),
        inspection);
      addRect(
        reference.bounds,
        "annotation-reference",
        title,
        confidence(reference.confidence),
        inspection);
    });
  });
}

function drawCompareOverlay(page) {
  if (!state.compare || !state.enabledLayers.has("compare")) {
    return;
  }

  compareDrawableLayers.forEach((descriptor) => {
    if (!state.enabledLayers.has(descriptor.layer)) {
      return;
    }

    const delta = state.compare.layers[descriptor.key];
    if (!delta) {
      return;
    }

    delta.removedItems
      .filter((item) => item.pageNumber === page.number && visibleBySourceLayer(item))
      .forEach((item) => drawCompareItem(item, descriptor, "removed"));

    delta.addedItems
      .filter((item) => item.pageNumber === page.number && visibleBySourceLayer(item))
      .forEach((item) => drawCompareItem(item, descriptor, "added"));
  });
}

function drawCompareItem(item, descriptor, status) {
  const title = `${status === "added" ? "Added" : "Removed"} ${descriptor.label.toLowerCase()} ${item.id || ""}`.trim();
  const inspection = describeCompareItem(item, descriptor, status);
  const className = `compare-geometry compare-${status}`;
  const opacity = status === "added" ? 0.88 : 0.82;

  switch (descriptor.geometry) {
    case "room":
      if (item.boundary?.length >= 3) {
        addPolygon(item.boundary, className, title, opacity, inspection);
      } else {
        addRect(item.bounds, className, title, opacity, inspection);
      }
      break;
    case "dimension":
      addRect(item.bounds, className, title, opacity, inspection);
      if (item.dimensionLine) {
        addLine(item.dimensionLine, `${className} compare-line`, title, opacity, inspection);
      }
      break;
    case "axis":
      addLine(item.line, `${className} compare-line`, title, opacity, inspection);
      break;
    case "wallLine":
      addLine(item.centerLine, `${className} compare-line`, title, opacity, inspection);
      break;
    case "point":
      addCircle(item.position, 6, `${className} compare-point`, title, opacity, inspection);
      break;
    default:
      addRect(item.bounds, className, title, opacity, inspection);
      break;
  }
}

function drawBenchmarkTargets(page) {
  const targets = filteredBenchmarkTargets(activeBenchmarkTargets());
  if (!targets.length || !state.enabledLayers.has("benchmarkTargets")) {
    return;
  }

  const labelSlots = new Map();
  targets
    .filter((target) => target.bounds && benchmarkTargetOnPage(target, page.number) && visibleBySourceLayer(target))
    .forEach((target) => {
      const className = [
        "benchmark-target",
        target.pageNumber == null ? "unpaged" : "",
        isLowConfidenceBenchmarkTarget(target) ? "low" : "",
        benchmarkTargetHasEvidence(target) ? "" : "missing-evidence",
        benchmarkTargetBoundsEdited(target) ? "bounds-edited" : "",
        target.isAdded ? "added" : "",
        target.isReviewQueueItem ? benchmarkReviewQueueKindClass(target.reviewQueueKind) : "",
        benchmarkTargetDecision(target)
      ].filter(Boolean).join(" ");
      const title = `${target.detectorLabel} benchmark target ${target.id}`;
      addRect(
        target.bounds,
        className,
        title,
        benchmarkTargetOpacity(target),
        describeBenchmarkTarget(target));
      addBenchmarkTargetLabel(target, labelSlots);
    });
}

function addBenchmarkTargetLabel(target, labelSlots = new Map()) {
  if (!target.bounds) {
    return;
  }

  const baseX = Number(target.bounds.x ?? 0) + 4;
  const baseY = Math.max(10, Number(target.bounds.y ?? 0) - 5);
  const slotKey = `${Math.round(baseX / 32)}:${Math.round(baseY / 12)}`;
  const slotIndex = labelSlots.get(slotKey) ?? 0;
  labelSlots.set(slotKey, slotIndex + 1);

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", baseX);
  label.setAttribute("y", Math.max(10, baseY - (slotIndex * 12)));
  label.setAttribute("class", "benchmark-target-label");
  label.textContent = benchmarkDetectorShortLabel(target.detectorLabel);
  elements.overlay.appendChild(label);
}

function addScanReviewQueueLabel(item, selected, labelSlots = new Map()) {
  const bounds = normalizeRect(item.bounds);
  const text = scanReviewQueueOverlayLabel(item);
  if (!bounds || !text) {
    return;
  }

  const baseX = Number(bounds.x ?? 0) + 4;
  const baseY = Math.max(10, Number(bounds.y ?? 0) - 5);
  const slotKey = `${Math.round(baseX / 42)}:${Math.round(baseY / 12)}`;
  const slotIndex = labelSlots.get(slotKey) ?? 0;
  labelSlots.set(slotKey, slotIndex + 1);

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", baseX);
  label.setAttribute("y", Math.max(10, baseY - (slotIndex * 12)));
  label.setAttribute("class", `scan-review-label scan-review-label-${scanReviewQueueKindKey(item)}`);
  label.textContent = text;
  addTitle(label, `${scanReviewQueueKindLabel(item.kind)} - ${selected.id || item.id || ""}`.trim());
  elements.overlay.appendChild(label);
}

function addSurfacePatternLabel(pattern, title) {
  const bounds = normalizeRect(pattern.bounds);
  if (!bounds) {
    return;
  }

  const label = document.createElementNS("http://www.w3.org/2000/svg", "text");
  label.setAttribute("x", Number(bounds.x ?? 0) + 4);
  label.setAttribute("y", Math.max(10, Number(bounds.y ?? 0) + 12));
  label.setAttribute("class", "surface-pattern-label");
  label.textContent = surfacePatternShortLabel(pattern);
  addTitle(label, title);
  elements.overlay.appendChild(label);
}

function surfacePatternShortLabel(pattern) {
  const kind = String(pattern.kind || "");
  const suffix = String(pattern.id || "").match(/(\d+)$/)?.[1];
  const number = suffix ? ` ${Number(suffix)}` : "";
  if (kind.includes("OrthogonalGrid")) {
    return `SP grid${number}`;
  }

  if (kind.includes("ParallelBand")) {
    return `SP band${number}`;
  }

  return `SP${number}`;
}

function drawManualBenchmarkTargetDraft(page) {
  const draft = state.benchmarkManualTargetDraft;
  if (!state.benchmarkManifest || !draft?.bounds || draft.pageNumber !== page.number) {
    return;
  }

  addRect(
    draft.bounds,
    ["benchmark-manual-draft", draft.drawing ? "drawing" : ""].filter(Boolean).join(" "),
    "Manual benchmark target draft",
    0.88,
    {
      type: "manual benchmark draft",
      id: draft.id || "manual target",
      kind: benchmarkDetectorLabel(draft.detectorKey),
      confidence: 1,
      bounds: draft.bounds,
      pageNumber: draft.pageNumber,
      evidence: ["Manual benchmark target draft."]
    });
}

function benchmarkDetectorShortLabel(label) {
  return String(label || "Target")
    .replace(/\s+targets?$/i, "")
    .split(/\s+/)
    .map((part) => part.slice(0, 4))
    .join(" ");
}

function benchmarkTargetOpacity(target) {
  if (isLowConfidenceBenchmarkTarget(target)) {
    return 0.58;
  }

  return Math.max(0.52, Math.min(0.92, Number(target.confidence ?? 0.78)));
}

function benchmarkTargetOnPage(target, pageNumber) {
  return target.pageNumber == null || target.pageNumber === pageNumber;
}

function isLowConfidenceBenchmarkTarget(target) {
  return Number.isFinite(Number(target.confidence)) && Number(target.confidence) < 0.6;
}

function benchmarkTargetDecision(targetOrKey) {
  const key = typeof targetOrKey === "string" ? targetOrKey : targetOrKey.reviewKey;
  return state.benchmarkReviewDecisions.get(key)?.decision || "";
}

function benchmarkTargetDecisionLabel(decision) {
  switch (decision) {
    case "accepted":
      return "Accepted";
    case "rejected":
      return "Rejected";
    case "needsReview":
      return "Needs review";
    default:
      return "Unreviewed";
  }
}

function benchmarkTargetBoundsEdited(targetOrKey) {
  const key = typeof targetOrKey === "string" ? targetOrKey : targetOrKey.reviewKey;
  return state.benchmarkTargetEdits.has(key);
}

function benchmarkTargetDeleted(targetOrKey) {
  const key = typeof targetOrKey === "string" ? targetOrKey : targetOrKey.reviewKey;
  return state.benchmarkDeletedTargets.has(key);
}

function activeBenchmarkTargets() {
  return state.benchmarkTargets.filter((target) => !benchmarkTargetDeleted(target));
}

function resetBenchmarkFilters() {
  return { ...defaultBenchmarkFilters };
}

function resetBenchmarkManualTargetDraft(overrides = {}) {
  return {
    ...defaultBenchmarkManualTargetDraft,
    ...overrides,
    bounds: overrides.bounds ? clonePlain(overrides.bounds) : null,
    drawing: Boolean(overrides.drawing)
  };
}

function filteredBenchmarkTargets(targets) {
  const filters = state.benchmarkFilters ?? resetBenchmarkFilters();
  const query = String(filters.query || "").trim().toLowerCase();
  return targets.filter((target) => {
    if (filters.detector !== "all" && target.detectorKey !== filters.detector) {
      return false;
    }

    if (filters.queueKind !== "all" && benchmarkReviewQueueKind(target.reviewQueueKind) !== filters.queueKind) {
      return false;
    }

    const decision = benchmarkTargetDecision(target) || "unreviewed";
    if (filters.status !== "all" && decision !== filters.status) {
      return false;
    }

    if (!benchmarkTargetMatchesIssueFilter(target, filters.issue)) {
      return false;
    }

    if (!benchmarkTargetMatchesPageFilter(target, filters.page)) {
      return false;
    }

    if (query && !benchmarkTargetSearchText(target).includes(query)) {
      return false;
    }

    return true;
  });
}

function benchmarkTargetMatchesIssueFilter(target, issue) {
  switch (issue) {
    case "missingBounds":
      return !target.bounds;
    case "lowConfidence":
      return isLowConfidenceBenchmarkTarget(target);
    case "missingEvidence":
      return !benchmarkTargetHasEvidence(target);
    case "boundsEdited":
      return benchmarkTargetBoundsEdited(target);
    case "added":
      return Boolean(target.isAdded);
    default:
      return true;
  }
}

function benchmarkTargetMatchesPageFilter(target, page) {
  switch (page) {
    case "current":
      return target.pageNumber === state.currentPage;
    case "unpaged":
      return target.pageNumber == null;
    default:
      return true;
  }
}

function benchmarkTargetHasEvidence(target) {
  return Boolean(target.evidence?.length || target.sourcePrimitiveIds?.length || target.sourceLayers?.length);
}

function benchmarkTargetSearchText(target) {
  return [
    target.id,
    target.detectorLabel,
    target.detectorKey,
    target.fixtureId,
    target.fixtureName,
    target.sourcePath,
    target.reviewQueueKind,
    target.recommendedAction,
    target.sourceLayers?.join(" "),
    target.sourcePrimitiveIds?.join(" "),
    target.detectedTags?.join(" "),
    target.evidence?.join(" "),
    benchmarkTargetCriteria(target)
  ].filter(Boolean).join(" ").toLowerCase();
}

function describeCompareItem(item, descriptor, status) {
  const bounds = normalizeRect(item.bounds) ?? boundsFromDetectionGeometry(item);
  return {
    type: `compare ${status}`,
    id: item.id || comparableItemKey(item, descriptor.key),
    kind: descriptor.label,
    confidence: item.confidence,
    bounds,
    line: item.centerLine ?? item.line ?? item.dimensionLine ?? null,
    boundary: item.boundary || [],
    sourceLayers: item.sourceLayers || [],
    sourcePrimitiveIds: item.sourcePrimitiveIds || item.labelSourcePrimitiveIds || [],
    pageNumber: item.pageNumber,
    measurement: describeMeasurement(item),
    placementSummary: describeOpeningPlacement(item.placement),
    metricPlacementSummary: describeMetricOpeningPlacement(item.placement),
    scaleGroupId: item.measurementScaleGroupId || "",
    evidence: item.evidence || [],
    pairEvidence: item.pairEvidence || null,
    nearbyText: item.nearbyText || [],
    hostWallIds: item.hostWallIds || item.adjacentWallIds || [],
    connectedRoomLinks: item.connectedRoomLinks || [],
    roomId: item.roomId || "",
    roomLabel: item.roomLabel || "",
    swing: describeSwing(item),
    metadata: status === "added"
      ? `Only in candidate: ${state.compare.candidateLabel}`
      : `Only in baseline: ${state.compare.baselineLabel}`
  };
}

function addTitle(element, text) {
  const title = document.createElementNS("http://www.w3.org/2000/svg", "title");
  title.textContent = text;
  element.appendChild(title);
}

function clearOverlay() {
  elements.overlay.replaceChildren();
}

function onCurrentPage(item) {
  return item.pageNumber === state.currentPage;
}

function visibleBySourceLayer(item) {
  if (!item.sourceLayers?.length) {
    return true;
  }

  if (!state.enabledSourceLayers.size && !(state.scan?.layers?.length)) {
    return true;
  }

  return item.sourceLayers.some((layer) => state.enabledSourceLayers.has(layerKey(layer)));
}

function attachInspection(element, item) {
  if (!item) {
    return;
  }

  element.addEventListener("click", (event) => {
    event.stopPropagation();
    state.selectedItem = item;
    setSelection(item);
    setBenchmarkDetails();
  });
}

elements.overlay.addEventListener("mousedown", (event) => {
  if (!state.benchmarkManifest || !state.benchmarkManualTargetDraft?.drawing) {
    return;
  }

  const point = overlayPointFromEvent(event);
  if (!point) {
    return;
  }

  event.preventDefault();
  state.benchmarkDrawBox = {
    start: point,
    pageNumber: state.currentPage
  };
  state.benchmarkManualTargetDraft.pageNumber = state.currentPage;
  state.benchmarkManualTargetDraft.bounds = normalizeManualBounds(point, point);
  drawOverlay();
});

elements.overlay.addEventListener("mousemove", (event) => {
  const point = overlayPointFromEvent(event);
  setCursorCoordinates(point);

  if (!state.benchmarkDrawBox || !state.benchmarkManualTargetDraft?.drawing) {
    return;
  }

  if (!point) {
    return;
  }

  event.preventDefault();
  state.benchmarkManualTargetDraft.pageNumber = state.benchmarkDrawBox.pageNumber;
  state.benchmarkManualTargetDraft.bounds = normalizeManualBounds(state.benchmarkDrawBox.start, point);
  drawOverlay();
});

elements.overlay.addEventListener("mouseleave", () => {
  setCursorCoordinates();
});

elements.overlay.addEventListener("mouseup", (event) => {
  if (!state.benchmarkDrawBox || !state.benchmarkManualTargetDraft?.drawing) {
    return;
  }

  const point = overlayPointFromEvent(event);
  if (point) {
    state.benchmarkManualTargetDraft.bounds = normalizeManualBounds(state.benchmarkDrawBox.start, point);
  }

  state.benchmarkManualTargetDraft.pageNumber = state.benchmarkDrawBox.pageNumber;
  state.benchmarkManualTargetDraft.drawing = false;
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = true;
  setStatus("Manual target box captured");
  refreshBenchmarkReviewUi();
});

elements.overlay.addEventListener("click", () => {
  if (state.benchmarkSuppressNextOverlayClick) {
    state.benchmarkSuppressNextOverlayClick = false;
    return;
  }

  if (state.benchmarkManualTargetDraft?.drawing) {
    return;
  }

  state.selectedItem = null;
  setSelection();
  setBenchmarkDetails();
});

function overlayPointFromEvent(event) {
  const page = currentPageDefinition();
  if (!page) {
    return null;
  }

  const rect = elements.overlay.getBoundingClientRect();
  if (!rect.width || !rect.height) {
    return null;
  }

  const x = ((event.clientX - rect.left) / rect.width) * page.width;
  const y = ((event.clientY - rect.top) / rect.height) * page.height;
  return {
    x: Math.max(0, Math.min(page.width, x)),
    y: Math.max(0, Math.min(page.height, y))
  };
}

function normalizeManualBounds(first, second) {
  return normalizeRect({
    x: Math.min(first.x, second.x),
    y: Math.min(first.y, second.y),
    width: Math.abs(second.x - first.x),
    height: Math.abs(second.y - first.y)
  });
}

function describeItem(type, item) {
  const bounds = normalizeRect(item.bounds) ?? boundsFromDetectionGeometry(item);
  const pageNumber = item.pageNumber;
  const reviewCropBounds = type === "object"
    ? normalizeRect(item.reviewCropBounds) ?? buildReviewCropBounds(bounds, pageNumber, state.scan)
    : normalizeRect(item.reviewCropBounds);

  return {
    type,
    id: item.id,
    kind: item.useKind || item.category || item.kind || item.operation || item.type || item.detectionKind || item.label || "",
    confidence: item.confidence,
    bounds,
    line: item.centerLine ?? item.line ?? item.dimensionLine ?? null,
    boundary: item.boundary || [],
    reviewCropBounds,
    detectedTag: item.detectedTag || "",
    detectedTagSourcePrimitiveId: item.detectedTagSourcePrimitiveId || "",
    visualAi: item.visualAi || null,
    sourceLayers: item.sourceLayers || [],
    sourcePrimitiveIds: item.sourcePrimitiveIds || item.labelSourcePrimitiveIds || [],
    pageNumber,
    measurement: describeMeasurement(item),
    placementSummary: describeOpeningPlacement(item.placement),
    metricPlacementSummary: describeMetricOpeningPlacement(item.placement),
    scaleGroupId: item.measurementScaleGroupId || "",
    topology: describeTopology(item),
    wallComponent: item.wallComponentId
      ? `${item.wallComponentId}${item.wallComponentKind ? ` (${item.wallComponentKind})` : ""}`
      : "",
    wallComponentId: item.wallComponentId || "",
    wallComponentKind: item.wallComponentKind || "",
    excludedFromStructuralTopology: Boolean(item.excludedFromStructuralTopology),
    reliability: item.reliability || null,
    reliabilitySummary: type === "wall" ? wallReliabilitySummary(item) : "",
    reliabilityReasons: type === "wall" ? wallReliabilityReasons(item) : [],
    evidence: item.evidence || [],
    pairEvidence: item.pairEvidence || null,
    hostWallIds: item.hostWallIds || item.adjacentWallIds || [],
    connectedRoomLinks: item.connectedRoomLinks || [],
    roomId: item.roomId || (item.connectedRoomIds?.length ? item.connectedRoomIds.join(" -> ") : ""),
    roomLabel: item.roomLabel || (item.connectedRoomLabels?.length ? item.connectedRoomLabels.join(" -> ") : ""),
    swing: describeSwing(item),
    metadata: describeGenericMetadata(item),
    benchmarkDraft: benchmarkDraftForDetection(type, item)
  };
}

function describeWallGraphRepairCandidate(candidate) {
  const gapDistance = candidate.gapDistanceDrawingUnits ?? candidate.gapDistance;
  return {
    ...describeItem("wall graph repair", {
      ...candidate,
      bounds: candidate.bounds,
      line: candidate.repairLine,
      kind: candidate.kind,
      confidence: candidate.confidence,
      sourcePrimitiveIds: candidate.sourcePrimitiveIds || [],
      sourceLayers: candidate.sourceLayers || [],
      evidence: candidate.evidence || []
    }),
    metadata: [
      candidate.suggestedAction ? `Action: ${candidate.suggestedAction}` : "",
      candidate.sourceNodeId ? `Source node: ${candidate.sourceNodeId}` : "",
      candidate.targetNodeId ? `Target node: ${candidate.targetNodeId}` : "",
      candidate.hostWallId ? `Host wall: ${candidate.hostWallId}` : "",
      candidate.severity ? `Severity: ${candidate.severity}` : "",
      candidate.importImpact ? `Import impact: ${candidate.importImpact}` : "",
      candidate.applicability ? `Applicability: ${candidate.applicability}` : "",
      Number.isFinite(Number(gapDistance)) ? `Gap: ${formatNumber(gapDistance)} drawing units` : "",
      candidate.gapDistanceMillimeters != null ? `Gap: ${formatNumber(candidate.gapDistanceMillimeters)} mm` : "",
      candidate.safeSnapDistanceDrawingUnits != null || candidate.safeSnapDistance != null
        ? `Safe snap: ${formatNumber(candidate.safeSnapDistanceDrawingUnits ?? candidate.safeSnapDistance)} drawing units`
        : "",
      candidate.excessDistanceBeyondSafeSnapDrawingUnits != null || candidate.excessDistanceBeyondSafeSnap != null
        ? `Excess beyond safe snap: ${formatNumber(candidate.excessDistanceBeyondSafeSnapDrawingUnits ?? candidate.excessDistanceBeyondSafeSnap)} drawing units`
        : "",
      candidate.reviewDistanceLimitDrawingUnits != null || candidate.reviewDistanceLimit != null
        ? `Review limit: ${formatNumber(candidate.reviewDistanceLimitDrawingUnits ?? candidate.reviewDistanceLimit)} drawing units`
        : "",
      candidate.wallIds?.length ? `Walls: ${candidate.wallIds.join(", ")}` : "",
      candidate.requiresReview ? "Requires review before applying" : ""
    ].filter(Boolean).join("\n")
  };
}

function describeSurfacePattern(pattern) {
  const spacingParts = [
    pattern.horizontalMedianSpacing != null ? `h=${formatNumber(pattern.horizontalMedianSpacing)}` : "",
    pattern.verticalMedianSpacing != null ? `v=${formatNumber(pattern.verticalMedianSpacing)}` : "",
    pattern.medianSpacing != null ? `${formatNumber(pattern.medianSpacing)}` : ""
  ].filter(Boolean);

  return {
    ...describeItem("surface pattern", {
      ...pattern,
      kind: pattern.kind,
      bounds: pattern.bounds,
      confidence: pattern.confidence,
      sourcePrimitiveIds: pattern.sourcePrimitiveIds || [],
      sourceLayers: pattern.sourceLayers || [],
      evidence: pattern.evidence || []
    }),
    topology: [
      pattern.excludedFromWallDetection ? "excluded from wall detection" : "",
      pattern.excludedFromStructuralTopology ? "excluded from structural topology" : "",
      pattern.requiresReview ? "requires visual review" : ""
    ].filter(Boolean).join(", "),
    metadata: [
      pattern.orientation ? `Orientation: ${pattern.orientation}` : "",
      pattern.sourceRegionId ? `Region: ${pattern.sourceRegionId}` : "",
      Number.isFinite(Number(pattern.lineCount)) ? `Lines: ${pattern.lineCount}` : "",
      Number.isFinite(Number(pattern.horizontalLineCount)) ? `Horizontal: ${pattern.horizontalLineCount}` : "",
      Number.isFinite(Number(pattern.verticalLineCount)) ? `Vertical: ${pattern.verticalLineCount}` : "",
      Number.isFinite(Number(pattern.intersectionCount)) ? `Intersections: ${pattern.intersectionCount}` : "",
      spacingParts.length ? `Median spacing: ${spacingParts.join(", ")} drawing units` : ""
    ].filter(Boolean).join("\n")
  };
}

function describeObjectAggregate(aggregate) {
  const bounds = normalizeRect(aggregate.bounds) ?? boundsFromDetectionGeometry(aggregate);
  const composition = aggregate.composition || {};
  const childCategorySummary = formatKvemoCountSummary(composition.categoryCounts || []);
  const childSourceSummary = formatKvemoCountSummary(composition.sourceKindCounts || []);
  const wallComponentSummary = formatKvemoCountSummary(composition.sourceWallComponentKindCounts || []);
  return {
    type: "object aggregate",
    id: aggregate.id,
    kind: aggregate.label || aggregate.category || aggregate.kind || "",
    confidence: aggregate.confidence,
    bounds,
    line: null,
    boundary: [],
    reviewCropBounds: buildReviewCropBounds(bounds, aggregate.pageNumber, state.scan),
    detectedTag: "",
    detectedTagSourcePrimitiveId: "",
    visualAi: null,
    sourceLayers: aggregate.sourceLayers || [],
    sourcePrimitiveIds: aggregate.sourcePrimitiveIds || [],
    pageNumber: aggregate.pageNumber,
    measurement: bounds
      ? `${formatNumber(bounds.width)} x ${formatNumber(bounds.height)} drawing units`
      : "",
    scaleGroupId: "",
    topology: "",
    wallComponent: "",
    wallComponentId: "",
    wallComponentKind: "",
    excludedFromStructuralTopology: false,
    evidence: aggregate.evidence || [],
    pairEvidence: null,
    hostWallIds: [],
    connectedRoomLinks: [],
    roomId: aggregate.roomId || "",
    roomLabel: aggregate.roomLabel || "",
    nearbyText: aggregate.nearbyText || [],
    swing: "",
    metadata: [
      `${aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0} child object${(aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0) === 1 ? "" : "s"}`,
      knownValue(aggregate.routingInfluence) ? `routing ${aggregate.routingInfluence}` : "",
      knownValue(aggregate.structuralInfluence) ? `structure ${aggregate.structuralInfluence}` : "",
      aggregate.suppressChildObjectsForRouting ? "suppress child objects for routing" : "",
      knownValue(aggregate.roomUseEvidence) ? `room evidence ${aggregate.roomUseEvidence}` : "",
      childCategorySummary ? `child categories ${childCategorySummary}` : "",
      childSourceSummary ? `child sources ${childSourceSummary}` : "",
      wallComponentSummary ? `child wall components ${wallComponentSummary}` : "",
      composition.sourceWallComponentIds?.length ? `source wall ids ${composition.sourceWallComponentIds.slice(0, 6).join(", ")}${composition.sourceWallComponentIds.length > 6 ? ", ..." : ""}` : "",
      aggregate.requiresReview ? "review required" : "ready",
      aggregate.objectGroupIds?.length ? `object groups ${aggregate.objectGroupIds.join(", ")}` : "",
      aggregate.childObjectIds?.length ? `child ids ${aggregate.childObjectIds.slice(0, 8).join(", ")}${aggregate.childObjectIds.length > 8 ? ", ..." : ""}` : ""
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("object aggregate", aggregate)
  };
}

function describeRoutingItem(type, item) {
  const bounds = normalizeRect(item.bounds) ?? normalizeRect(item.candidateBounds) ?? boundsFromDetectionGeometry(item);
  const metadata = [
    item.sourceKind ? `source ${item.sourceKind}` : "",
    item.sourceId ? `source id ${item.sourceId}` : "",
    item.objectCandidateId ? `child object ${item.objectCandidateId}` : "",
    item.suppressedByAggregateId ? `suppressed by ${item.suppressedByAggregateId}` : "",
    item.reason ? `reason ${item.reason}` : "",
    item.action ? `action ${item.action}` : "",
    item.replacementRoutingObstacleId ? `replacement ${item.replacementRoutingObstacleId}` : "",
    item.roomUseHintId ? `room-use hint ${item.roomUseHintId}` : "",
    item.obstacleKind ? `obstacle ${item.obstacleKind}` : "",
    item.routingInfluence ? `routing ${item.routingInfluence}` : "",
    item.aggregateRoutingInfluence ? `aggregate routing ${item.aggregateRoutingInfluence}` : "",
    item.structuralInfluence ? `structure ${item.structuralInfluence}` : "",
    item.aggregateStructuralInfluence ? `aggregate structure ${item.aggregateStructuralInfluence}` : "",
    item.candidateCategory ? `child category ${item.candidateCategory}` : "",
    item.candidateKind ? `child kind ${item.candidateKind}` : "",
    item.suppressesChildObjects ? "suppresses child objects" : "",
    item.roomUseKind ? `room use ${item.roomUseKind}` : "",
    item.type ? `opening ${item.type}` : "",
    item.operation ? `operation ${item.operation}` : "",
    item.widthMillimeters != null ? `${formatNumber(item.widthMillimeters)} mm` : "",
    item.drawingWidth != null ? `${formatNumber(item.drawingWidth)} drawing units` : "",
    item.wallComponentId ? `component ${item.wallComponentId}` : "",
    item.wallComponentKind ? `component kind ${item.wallComponentKind}` : ""
  ].filter(Boolean).join(" | ");

  return {
    type,
    id: item.id,
    kind: item.obstacleKind || item.roomUseKind || item.action || item.type || item.sourceKind || "",
    confidence: item.confidence,
    bounds,
    line: item.centerLine ?? null,
    boundary: [],
    reviewCropBounds: bounds ? buildReviewCropBounds(bounds, item.pageNumber, state.scan) : null,
    detectedTag: "",
    detectedTagSourcePrimitiveId: "",
    visualAi: null,
    sourceLayers: item.sourceLayers || [],
    sourcePrimitiveIds: item.sourcePrimitiveIds || [],
    pageNumber: item.pageNumber,
    measurement: bounds
      ? `${formatNumber(bounds.width)} x ${formatNumber(bounds.height)} drawing units`
      : item.drawingLength != null
        ? `${formatNumber(item.drawingLength)} drawing units`
        : "",
    placementSummary: describeOpeningPlacement(item.placement),
    metricPlacementSummary: describeMetricOpeningPlacement(item.placement),
    scaleGroupId: item.measurementScaleGroupId || "",
    topology: "",
    wallComponent: item.wallComponentId
      ? `${item.wallComponentId}${item.wallComponentKind ? ` (${item.wallComponentKind})` : ""}`
      : "",
    wallComponentId: item.wallComponentId || "",
    wallComponentKind: item.wallComponentKind || "",
    excludedFromStructuralTopology: Boolean(item.excludedFromStructuralTopology),
    evidence: item.evidence || [],
    pairEvidence: null,
    hostWallIds: item.hostWallIds || [],
    connectedRoomLinks: [],
    roomId: item.roomId || (item.connectedRoomIds?.length ? item.connectedRoomIds.join(" -> ") : ""),
    roomLabel: item.roomLabel || (item.connectedRoomLabels?.length ? item.connectedRoomLabels.join(" -> ") : ""),
    swing: "",
    metadata,
    benchmarkDraft: benchmarkDraftForDetection(type, item)
  };
}

function describeGenericMetadata(item) {
  const parts = [];
  if (item.wallComponentKind) {
    parts.push(`component ${item.wallComponentKind}`);
  }

  if (item.excludedFromStructuralTopology) {
    parts.push("excluded from structural topology");
  }

  if (item.degree != null) {
    parts.push(`degree ${item.degree}`);
  }

  if (item.directions?.length) {
    parts.push(`directions ${item.directions.join(", ")}`);
  }

  if (item.connectedRoomLabels?.length) {
    parts.push(`connected rooms ${item.connectedRoomLabels.join(" -> ")}`);
  } else if (item.connectedRoomIds?.length) {
    parts.push(`connected rooms ${item.connectedRoomIds.join(" -> ")}`);
  }

  if (item.roomAdjacencyIds?.length) {
    parts.push(`room links ${item.roomAdjacencyIds.join(", ")}`);
  }

  if (item.connectedRoomLinks?.length) {
    parts.push(`${item.connectedRoomLinks.length} room connection link${item.connectedRoomLinks.length === 1 ? "" : "s"}`);
  }

  if (item.useKind) {
    parts.push(`room use ${item.useKind}`);
  }

  if (item.count != null) {
    parts.push(`${item.count} candidate${item.count === 1 ? "" : "s"}`);
  }

  if (item.requiresReview != null) {
    parts.push(item.requiresReview ? "review required" : "inventory group");
  }

  if (item.reliability) {
    const summary = wallReliabilitySummary(item);
    if (summary) {
      parts.push(`reliability ${summary}`);
    }

    const reasons = wallReliabilityReasons(item);
    if (reasons.length) {
      parts.push(`reliability reasons ${reasons.join("; ")}`);
    }
  }

  if (item.pageNumbers?.length) {
    parts.push(`pages ${item.pageNumbers.join(", ")}`);
  }

  if (item.candidateIds?.length) {
    parts.push(`${item.candidateIds.length} candidate ids`);
  }

  if (item.detectedTag) {
    parts.push(`detected tag ${item.detectedTag}`);
  }

  if (item.detectedTags?.length) {
    parts.push(`detected tags ${item.detectedTags.join(", ")}`);
  }

  const ai = item.visualAi;
  if (ai) {
    parts.push(`visual AI ${ai.label} ${formatPercent(ai.confidence)} ${ai.modelName || "model"} ${ai.modelVersion || ""}`.trim());
    if (ai.cropBounds) {
      parts.push(`AI crop ${rectSignature(ai.cropBounds)}`);
    }
  }

  if (item.nearbyText?.length) {
    parts.push(`nearby text ${item.nearbyText.map((text) => text.text).slice(0, 4).join(", ")}`);
  }

  if (item.reviewCropBounds) {
    parts.push(`review crop ${rectSignature(item.reviewCropBounds)}`);
  }

  if (item.pairEvidence) {
    parts.push(`pair separation ${formatNumber(item.pairEvidence.faceSeparation)}`);
    parts.push(`pair overlap ${formatNumber(item.pairEvidence.overlapRatio)}`);
  }

  return parts.join(" | ");
}

function describeTopology(item) {
  if (item.excludedFromStructuralTopology) {
    return "Excluded from room/opening topology";
  }

  if (item.wallComponentKind) {
    return `Participates as ${item.wallComponentKind}`;
  }

  return "";
}

function wallClassName(wall) {
  const classes = ["wall"];
  classes.push(wallTypeClassName(wall));

  switch (wall.wallComponentKind) {
    case "MainStructural":
      classes.push("wall-main");
      break;
    case "SecondaryStructural":
      classes.push("wall-secondary");
      break;
    case "ObjectLikeIsland":
      classes.push("wall-object-like");
      break;
    case "IsolatedFragment":
      classes.push("wall-fragment");
      break;
    default:
      break;
  }

  if (wall.excludedFromStructuralTopology) {
    classes.push("wall-excluded");
  }

  if (wallCoordinateBlocked(wall)) {
    classes.push("wall-blocked");
  } else if (wallRequiresReliabilityReview(wall)) {
    classes.push("wall-review");
  }

  return classes.join(" ");
}

function wallTypeClassName(wall) {
  switch (String(wall?.wallType || "").toLowerCase()) {
    case "exterior":
      return "wall-exterior";
    case "interior":
      return "wall-interior";
    default:
      return "wall-unknown";
  }
}

function wallDrawOpacity(wall) {
  const baseOpacity = confidence(wall?.confidence);
  if (wall?.excludedFromStructuralTopology) {
    return Math.max(0.12, baseOpacity * 0.32);
  }

  if (wall?.wallComponentKind === "IsolatedFragment") {
    return Math.max(0.2, baseOpacity * 0.55);
  }

  return baseOpacity;
}

function wallRequiresReliabilityReview(wall) {
  if (wall?.evidenceAssessment?.requiresReview === true || wall?.requiresReview === true) {
    return true;
  }

  const reliability = wall?.reliability;
  if (!reliability?.requiresReview) {
    return false;
  }

  if (reliability.readyForCoordinatePlacement === false) {
    return true;
  }

  const reasons = normalizeStringArray(reliability.reasons).map((reason) => reason.toLowerCase());
  return reasons.some((reason) => !reason.includes("metric") && !reason.includes("scale"));
}

function wallCoordinateBlocked(wall) {
  if (wallHasTopologyImportBlockedRepair(wall)) {
    return true;
  }

  if (wall?.readyForCoordinatePlacement === false) {
    return true;
  }

  if (wall?.reliability) {
    return wall.reliability.readyForCoordinatePlacement === false;
  }

  return wall?.evidenceAssessment && wall.evidenceAssessment.placementReady === false;
}

function wallHasTopologyImportBlockedRepair(wall, scan = state.scan) {
  if (!wall?.id || !Array.isArray(scan?.wallGraphRepairCandidates)) {
    return false;
  }

  const wallId = String(wall.id);
  const repairIds = new Set(normalizeStringArray(wall.wallGraphRepairCandidateIds ?? wall.repairCandidateIds));
  return scan.wallGraphRepairCandidates.some((candidate) => {
    if (String(candidate?.importImpact || "").toLowerCase() !== "topologyimportblocked") {
      return false;
    }

    if (repairIds.size > 0 && repairIds.has(String(candidate.id || ""))) {
      return !wallGraphRepairIsEndpointToWallHost(candidate, wallId);
    }

    return wallGraphRepairCoordinateImpactsWall(candidate, wallId);
  });
}

function wallGraphRepairCoordinateImpactsWall(candidate, wallId) {
  if (!candidate || !wallId) {
    return false;
  }

  if (wallGraphRepairIsEndpointToWallHost(candidate, wallId)) {
    return false;
  }

  const normalizedWallId = String(wallId);
  if (normalizeStringArray(candidate?.wallIds).includes(normalizedWallId)) {
    return true;
  }

  const kind = String(candidate?.kind || "").toLowerCase();
  const hostWallId = String(candidate?.hostWallId || "");
  return kind !== "endpointtowall" && hostWallId === normalizedWallId;
}

function wallGraphRepairIsEndpointToWallHost(candidate, wallId) {
  const kind = String(candidate?.kind || "").toLowerCase();
  const hostWallId = String(candidate?.hostWallId || "");
  return kind === "endpointtowall" && hostWallId === String(wallId);
}

function wallReliabilitySummary(wall) {
  if (!wall?.reliability && !wall?.evidenceAssessment) {
    return "";
  }

  if (!wall?.reliability) {
    const assessment = wall.evidenceAssessment;
    return [
      assessment.placementReady === false ? "coordinate blocked" : "coordinate ready",
      assessment.requiresReview ? "review required" : "review clear",
      assessment.category ? `evidence ${assessment.category}` : "",
      assessment.confidence == null ? "" : `rel ${formatNumber(assessment.confidence)}`
    ].filter(Boolean).join(" / ");
  }

  const reliability = wall.reliability;
  return [
    reliability.readyForCoordinatePlacement === false ? "coordinate blocked" : "coordinate ready",
    reliability.readyForMetricPlacement === false ? "metric blocked" : "metric ready",
    reliability.requiresReview ? "review required" : "review clear",
    reliability.confidence == null ? "" : `rel ${formatNumber(reliability.confidence)}`
  ].filter(Boolean).join(" / ");
}

function wallReliabilityReasons(wall) {
  const reasons = normalizeStringArray(wall?.reliability?.reasons);
  if (reasons.length) {
    return reasons;
  }

  return normalizeStringArray(wall?.evidenceAssessment?.evidence);
}

function wallRawDrawLines(wall) {
  return wall?.centerLine?.start && wall?.centerLine?.end
    ? [{
      id: wall.id,
      centerLine: wall.centerLine,
      confidence: wall.confidence,
      isRawWall: true
    }]
    : [];
}

function wallCleanTopologySpans(wall) {
  const spans = Array.isArray(wall?.topologySpans)
    ? wall.topologySpans
      .filter((span) => span?.centerLine?.start && span?.centerLine?.end)
      .map((span, index) => normalizeViewerWallTopologySpan(wall, {
        ...span,
        id: span.id || `${wall.id}:topology-span:${index + 1}`,
        centerLine: span.centerLine,
        confidence: span.confidence ?? wall.confidence,
        wallGraphEdgeId: span.wallGraphEdgeId || null,
        isTopologySpan: true
      }))
      .filter(Boolean)
    : [];

  return mergeViewerCleanTopologyRuns(wall, spans);
}

function normalizeViewerWallTopologySpan(wall, span) {
  const wallLine = normalizeLine(wall?.centerLine ?? wall?.line);
  const spanLine = normalizeLine(span?.centerLine ?? span?.line);
  if (!spanLine) {
    return null;
  }

  if (!wallLine || lineLength(wallLine) <= 0.001) {
    return { ...span, centerLine: spanLine };
  }

  const orientation = dominantOrthogonalLineOrientation(wallLine);
  if (!orientation) {
    return { ...span, centerLine: spanLine };
  }

  const startParameter = viewerSpanSourceParameter(wallLine, spanLine.start, span.sourceWallStartParameter);
  const endParameter = viewerSpanSourceParameter(wallLine, spanLine.end, span.sourceWallEndParameter);
  const sourceStart = pointAtLine(wallLine, startParameter);
  const sourceEnd = pointAtLine(wallLine, endParameter);
  const axis = orientation === "horizontal"
    ? (wallLine.start.y + wallLine.end.y) / 2
    : (wallLine.start.x + wallLine.end.x) / 2;
  const centerLine = orientation === "horizontal"
    ? {
      start: { x: sourceStart.x, y: axis },
      end: { x: sourceEnd.x, y: axis }
    }
    : {
      start: { x: axis, y: sourceStart.y },
      end: { x: axis, y: sourceEnd.y }
    };
  const length = lineLength(centerLine);
  if (length <= 0.001) {
    return null;
  }

  if (length < viewerCleanWallMinSpanLength && viewerSpanLeavesSourceAxis(spanLine, orientation)) {
    return null;
  }

  const axisShift = viewerMaxSourceAxisShift(spanLine, centerLine, orientation);
  const evidence = axisShift > 0.001
    ? [
      ...(span.evidence ?? []),
      `viewer wall-span cleanup: projected graph span back to source wall axis by up to ${formatNumber(axisShift)} drawing units`
    ]
    : (span.evidence ?? []);

  return {
    ...span,
    centerLine,
    bounds: boundsFromLine(centerLine) ?? span.bounds,
    drawingLength: length,
    sourceWallStartParameter: startParameter,
    sourceWallEndParameter: endParameter,
    sourceWallCenterParameter: (startParameter + endParameter) / 2,
    sourceWallStartProjectionDistanceDrawingUnits: 0,
    sourceWallEndProjectionDistanceDrawingUnits: 0,
    evidence
  };
}

function dominantOrthogonalLineOrientation(line) {
  const dx = Math.abs(Number(line.end.x) - Number(line.start.x));
  const dy = Math.abs(Number(line.end.y) - Number(line.start.y));
  if (dy <= 1.5) {
    return "horizontal";
  }

  if (dx <= 1.5) {
    return "vertical";
  }

  const dominant = Math.max(dx, dy);
  const minor = Math.min(dx, dy);
  if (dominant <= 0.001
    || minor > viewerOrthogonalSkewDrawingUnits
    || minor / dominant > viewerOrthogonalSkewRatio) {
    return "";
  }

  return dx >= dy ? "horizontal" : "vertical";
}

function viewerSpanSourceParameter(wallLine, point, explicitValue) {
  const numeric = Number(explicitValue);
  if (Number.isFinite(numeric)) {
    return Math.min(1, Math.max(0, numeric));
  }

  return projectPointParameter(wallLine, point);
}

function viewerSpanLeavesSourceAxis(spanLine, orientation) {
  const dx = Math.abs(Number(spanLine.end.x) - Number(spanLine.start.x));
  const dy = Math.abs(Number(spanLine.end.y) - Number(spanLine.start.y));
  return orientation === "horizontal"
    ? dy > viewerOffAxisConnectorTolerance
    : dx > viewerOffAxisConnectorTolerance;
}

function viewerMaxSourceAxisShift(spanLine, projectedLine, orientation) {
  return orientation === "horizontal"
    ? Math.max(
      Math.abs(Number(spanLine.start.y) - Number(projectedLine.start.y)),
      Math.abs(Number(spanLine.end.y) - Number(projectedLine.end.y)))
    : Math.max(
      Math.abs(Number(spanLine.start.x) - Number(projectedLine.start.x)),
      Math.abs(Number(spanLine.end.x) - Number(projectedLine.end.x)));
}

function mergeViewerCleanTopologyRuns(wall, spans) {
  if (!spans.length) {
    return [];
  }

  const wallLine = normalizeLine(wall?.centerLine ?? wall?.line);
  const wallLength = lineLength(wallLine);
  if (!wallLine || wallLength <= 0.001) {
    return spans.filter((span) => viewerSpanLength(span) >= viewerCleanWallMinSpanLength);
  }

  const orientation = dominantOrthogonalLineOrientation(wallLine);
  if (!orientation) {
    return spans.filter((span) => viewerSpanLength(span) >= viewerCleanWallMinSpanLength);
  }

  const intervals = spans
    .map((span) => viewerCleanRunInterval(wallLine, span))
    .filter((interval) => interval.length >= viewerCleanWallMinSpanLength)
    .sort((first, second) => first.start - second.start);
  if (!intervals.length) {
    return [];
  }

  const merged = [];
  let current = intervals[0];
  for (let index = 1; index < intervals.length; index += 1) {
    const next = intervals[index];
    const gap = Math.max(0, next.start - current.end) * wallLength;
    if (gap <= viewerCleanWallMergeGap) {
      current = {
        ...current,
        end: Math.max(current.end, next.end),
        sourceSpanIds: [...new Set([...current.sourceSpanIds, ...next.sourceSpanIds])],
        evidence: [...new Set([...current.evidence, ...next.evidence])]
      };
      continue;
    }

    merged.push(current);
    current = next;
  }

  merged.push(current);
  return merged.map((interval, index) =>
    viewerCleanRunSpanFromInterval(wall, wallLine, orientation, wallLength, interval, index + 1));
}

function viewerSpanLength(span) {
  const explicitLength = Number(span?.drawingLength ?? span?.length);
  if (Number.isFinite(explicitLength) && explicitLength >= 0) {
    return explicitLength;
  }

  return lineLength(normalizeLine(span?.centerLine ?? span?.line));
}

function viewerCleanRunInterval(wallLine, span) {
  const spanLine = normalizeLine(span?.centerLine ?? span?.line);
  const startParameter = viewerSpanSourceParameter(wallLine, spanLine.start, span.sourceWallStartParameter);
  const endParameter = viewerSpanSourceParameter(wallLine, spanLine.end, span.sourceWallEndParameter);
  const start = Math.min(startParameter, endParameter);
  const end = Math.max(startParameter, endParameter);
  return {
    span,
    start,
    end,
    length: Math.max(0, end - start) * lineLength(wallLine),
    sourceSpanIds: [span.id].filter(Boolean),
    evidence: [
      ...(span.evidence ?? []),
      `viewer clean placement run includes source topology span ${span.id || "unknown"}`
    ]
  };
}

function viewerCleanRunSpanFromInterval(wall, wallLine, orientation, wallLength, interval, runIndex) {
  const centerLine = viewerSourceAxisLine(wallLine, orientation, interval.start, interval.end);
  const length = lineLength(centerLine);
  const evidence = [
    `viewer clean placement run merged ${interval.sourceSpanIds.length} topology span(s)`,
    ...interval.evidence
  ];

  return {
    ...interval.span,
    id: `${wall.id}:viewer-clean-run:${runIndex}`,
    wallGraphEdgeId: null,
    centerLine,
    bounds: boundsFromLine(centerLine) ?? interval.span.bounds,
    drawingLength: length,
    sourceWallStartParameter: interval.start,
    sourceWallEndParameter: interval.end,
    sourceWallCenterParameter: (interval.start + interval.end) / 2,
    sourceWallStartOffsetDrawingUnits: interval.start * wallLength,
    sourceWallEndOffsetDrawingUnits: interval.end * wallLength,
    sourceWallProjectedLengthDrawingUnits: length,
    sourceWallStartProjectionDistanceDrawingUnits: 0,
    sourceWallEndProjectionDistanceDrawingUnits: 0,
    evidence: [...new Set(evidence)]
  };
}

function viewerSourceAxisLine(wallLine, orientation, startParameter, endParameter) {
  const sourceStart = pointAtLine(wallLine, startParameter);
  const sourceEnd = pointAtLine(wallLine, endParameter);
  const axis = orientation === "horizontal"
    ? (wallLine.start.y + wallLine.end.y) / 2
    : (wallLine.start.x + wallLine.end.x) / 2;
  return orientation === "horizontal"
    ? {
      start: { x: sourceStart.x, y: axis },
      end: { x: sourceEnd.x, y: axis }
    }
    : {
      start: { x: axis, y: sourceStart.y },
      end: { x: axis, y: sourceEnd.y }
    };
}

function wallBodyFootprints(wall) {
  const solidSpans = wallSolidBodySpans(wall);
  if (solidSpans.length) {
    return solidSpans;
  }

  return wallCleanTopologySpans(wall)
    .map((span, index) => {
      const body = wallBodyPolygonFromTopologySpan(wall, span);
      if (!body?.polygon?.length) {
        return null;
      }

      return {
        id: `${span.id || `${wall.id}:topology-span:${index + 1}`}:body-footprint`,
        pageNumber: span.pageNumber ?? wall.pageNumber,
        bodyPolygon: body.polygon,
        bounds: boundsFromPoints(body.polygon),
        centerLine: span.centerLine,
        confidence: span.confidence ?? wall.confidence,
        thickness: Number(span.thickness ?? wall.thickness),
        geometrySource: body.geometrySource,
        evidence: [
          ...(span.evidence ?? []),
          `Viewer wall body footprint generated from ${body.geometrySource}`
        ]
      };
    })
    .filter(Boolean);
}

function wallSolidBodySpans(wall) {
  return Array.isArray(wall?.solidSpans)
    ? wall.solidSpans
      .filter((span) => Array.isArray(span?.bodyPolygon) && span.bodyPolygon.length >= 3)
      .map((span, index) => ({
        id: span.id || `${wall.id}:solid-span:${index + 1}:body-footprint`,
        pageNumber: span.pageNumber ?? wall.pageNumber,
        bodyPolygon: span.bodyPolygon,
        bounds: span.bodyBounds ?? boundsFromPoints(span.bodyPolygon),
        centerLine: span.centerLine ?? span.line ?? null,
        confidence: span.confidence ?? wall.confidence,
        thickness: span.thicknessDrawingUnits ?? wall.thickness,
        geometrySource: solidSpanGeometrySource(span),
        evidence: span.evidence ?? []
      }))
    : [];
}

function solidSpanGeometrySource(span) {
  const evidenceText = (span?.evidence ?? [])
    .map((item) => String(item ?? ""))
    .find((item) => item.includes("closed wall footprint ring from"));
  return evidenceText
    ? evidenceText.replace(/^.*closed wall footprint ring from\s+/i, "").replace(/\.$/, "")
    : "exported solid span body polygon";
}

function wallBodyPolygonFromTopologySpan(wall, span) {
  const pairPolygon = wallBodyPolygonFromPairEvidence(wall, span);
  if (pairPolygon?.length) {
    return {
      polygon: pairPolygon,
      geometrySource: "detected paired wall-face evidence"
    };
  }

  const fallbackPolygon = wallBodyPolygonFromCenterline(wall, span);
  return fallbackPolygon?.length
    ? {
      polygon: fallbackPolygon,
      geometrySource: "centerline plus wall thickness"
    }
    : null;
}

function wallBodyPolygonFromPairEvidence(wall, span) {
  const pair = wall?.pairEvidence;
  const firstFaceLine = normalizeLine(pair?.firstFaceLine);
  const secondFaceLine = normalizeLine(pair?.secondFaceLine);
  if (!firstFaceLine || !secondFaceLine) {
    return null;
  }

  const startParameter = wallSpanSourceParameter(wall, span, "start");
  const endParameter = wallSpanSourceParameter(wall, span, "end");
  if (!Number.isFinite(startParameter) || !Number.isFinite(endParameter)) {
    return null;
  }

  const firstStart = pointAtLine(firstFaceLine, startParameter);
  const firstEnd = pointAtLine(firstFaceLine, endParameter);
  const secondEnd = pointAtLine(secondFaceLine, endParameter);
  const secondStart = pointAtLine(secondFaceLine, startParameter);
  return [firstStart, firstEnd, secondEnd, secondStart, firstStart];
}

function wallBodyPolygonFromCenterline(wall, span) {
  const line = normalizeLine(span?.centerLine ?? span?.line);
  if (!line) {
    return null;
  }

  const vector = lineVector(line);
  const length = Math.hypot(vector.x, vector.y);
  const thickness = Math.max(0, Number(span?.thickness ?? wall?.thickness ?? 0));
  if (length <= 0.001 || thickness <= 0) {
    return null;
  }

  const normal = {
    x: -vector.y / length,
    y: vector.x / length
  };
  const offsetX = normal.x * thickness / 2;
  const offsetY = normal.y * thickness / 2;
  const startMinus = translatePoint(line.start, -offsetX, -offsetY);
  const endMinus = translatePoint(line.end, -offsetX, -offsetY);
  const endPlus = translatePoint(line.end, offsetX, offsetY);
  const startPlus = translatePoint(line.start, offsetX, offsetY);
  return [startMinus, endMinus, endPlus, startPlus, startMinus];
}

function wallSpanSourceParameter(wall, span, endpoint) {
  const explicitValue = endpoint === "start"
    ? span?.sourceWallStartParameter
    : span?.sourceWallEndParameter;
  const numeric = Number(explicitValue);
  if (Number.isFinite(numeric)) {
    return Math.min(1, Math.max(0, numeric));
  }

  const wallLine = normalizeLine(wall?.centerLine ?? wall?.line);
  const spanLine = normalizeLine(span?.centerLine ?? span?.line);
  if (!wallLine || !spanLine) {
    return null;
  }

  return projectPointParameter(wallLine, endpoint === "start" ? spanLine.start : spanLine.end);
}

function pointAtLine(line, parameter) {
  const t = Math.min(1, Math.max(0, Number(parameter)));
  return {
    x: line.start.x + ((line.end.x - line.start.x) * t),
    y: line.start.y + ((line.end.y - line.start.y) * t)
  };
}

function projectPointParameter(line, point) {
  const vector = lineVector(line);
  const lengthSquared = (vector.x * vector.x) + (vector.y * vector.y);
  if (lengthSquared <= 0.000001) {
    return null;
  }

  const t = ((point.x - line.start.x) * vector.x + (point.y - line.start.y) * vector.y) / lengthSquared;
  return Math.min(1, Math.max(0, t));
}

function lineVector(line) {
  return {
    x: Number(line.end.x) - Number(line.start.x),
    y: Number(line.end.y) - Number(line.start.y)
  };
}

function translatePoint(point, dx, dy) {
  return {
    x: Number(point.x) + dx,
    y: Number(point.y) + dy
  };
}

function wallVisualDrawLines(wall) {
  const topologySpans = wallCleanTopologySpans(wall);
  if (topologySpans.length) {
    return topologySpans;
  }

  return wallRawDrawLines(wall);
}

function wallTopologySpanClassName(wall) {
  const classes = ["wall-topology-span"];

  if (wall?.wallType === "Exterior") {
    classes.push("wall-topology-span-exterior");
  } else if (wall?.wallType === "Interior") {
    classes.push("wall-topology-span-interior");
  }

  if (wallRequiresReliabilityReview(wall)) {
    classes.push("wall-topology-span-review");
  }

  return classes.join(" ");
}

function wallBodyFootprintClassName(wall) {
  const classes = ["wall-body-footprint"];

  if (wall?.wallType === "Interior") {
    classes.push("wall-body-footprint-interior");
  }

  if (wallRequiresReliabilityReview(wall)) {
    classes.push("wall-body-footprint-review");
  }

  return classes.join(" ");
}

function wallReliabilityReviewWalls(scan = state.scan) {
  return (scan?.walls ?? [])
    .filter((wall) => wallRequiresReliabilityReview(wall) && !wallCoordinateBlocked(wall))
    .slice()
    .sort(compareWallReliabilityItems);
}

function wallReliabilityBlockedWalls(scan = state.scan) {
  return (scan?.walls ?? [])
    .filter(wallCoordinateBlocked)
    .slice()
    .sort(compareWallReliabilityItems);
}

function wallReliabilityCurrentPageWalls(scan = state.scan) {
  return [
    ...wallReliabilityBlockedWalls(scan),
    ...wallReliabilityReviewWalls(scan)
  ].filter(onCurrentPage);
}

function compareWallReliabilityItems(first, second) {
  const pageDelta = Number(first.pageNumber ?? 0) - Number(second.pageNumber ?? 0);
  if (pageDelta) {
    return pageDelta;
  }

  const firstReasonCount = wallReliabilityReasons(first).length;
  const secondReasonCount = wallReliabilityReasons(second).length;
  if (firstReasonCount !== secondReasonCount) {
    return secondReasonCount - firstReasonCount;
  }

  const confidenceDelta = Number(first.reliability?.confidence ?? first.confidence ?? 1)
    - Number(second.reliability?.confidence ?? second.confidence ?? 1);
  if (confidenceDelta) {
    return confidenceDelta;
  }

  return String(first.id || "").localeCompare(String(second.id || ""));
}

function wallReliabilityAuditListItems(scan = state.scan, limit = 14) {
  return wallReliabilityCurrentPageWalls(scan)
    .slice(0, limit)
    .map((wall) => ({
      title: `${wall.id || "wall"} - ${wallCoordinateBlocked(wall) ? "coordinate blocked" : "review required"}`,
      meta: [
        wallReliabilitySummary(wall),
        wall.bounds ? formatRectCoordinates(wall.bounds) : "",
        wallReliabilityReasons(wall).slice(0, 2).join("; ")
      ].filter(Boolean).join(" / "),
      onClick: () => selectWallReliabilityItem(wall)
    }));
}

function wallGraphRepairAuditListItems(scan = state.scan, limit = 14) {
  return (scan?.wallGraphRepairCandidates ?? [])
    .filter((candidate) => !candidate.pageNumber || candidate.pageNumber === state.currentPage)
    .slice()
    .sort((first, second) =>
      wallGraphRepairSeverityRank(second.severity) - wallGraphRepairSeverityRank(first.severity)
      || wallGraphRepairNumericValue(second, "excessDistanceBeyondSafeSnap") - wallGraphRepairNumericValue(first, "excessDistanceBeyondSafeSnap")
      || wallGraphRepairNumericValue(second, "gapDistance") - wallGraphRepairNumericValue(first, "gapDistance")
      || String(first.id || "").localeCompare(String(second.id || "")))
    .slice(0, limit)
    .map((candidate) => ({
      title: `${candidate.id || "repair"} - ${candidate.kind || "Wall graph repair"}`,
      meta: [
        candidate.suggestedAction || "",
        candidate.severity ? `${candidate.severity} severity` : "",
        candidate.importImpact || "",
        candidate.applicability || "",
        wallGraphRepairDistanceSummary(candidate)
      ].filter(Boolean).join(" / "),
      onClick: () => selectWallGraphRepairCandidate(candidate)
    }));
}

function wallGraphRepairSeverityRank(severity) {
  switch (String(severity || "").toLowerCase()) {
    case "high":
      return 3;
    case "medium":
      return 2;
    case "low":
      return 1;
    default:
      return 0;
  }
}

function wallGraphRepairNumericValue(candidate, field) {
  const drawingUnitField = `${field}DrawingUnits`;
  const value = candidate?.[drawingUnitField] ?? candidate?.[field];
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

function wallGraphRepairDistanceSummary(candidate) {
  const gap = wallGraphRepairNumericValue(candidate, "gapDistance");
  const safe = wallGraphRepairNumericValue(candidate, "safeSnapDistance");
  const excess = wallGraphRepairNumericValue(candidate, "excessDistanceBeyondSafeSnap");
  const reviewLimit = wallGraphRepairNumericValue(candidate, "reviewDistanceLimit");
  return [
    gap > 0 ? `gap ${formatNumber(gap)} du` : "",
    safe > 0 ? `safe ${formatNumber(safe)} du` : "",
    excess > 0 ? `excess ${formatNumber(excess)} du` : "within safe snap",
    reviewLimit > 0 ? `review limit ${formatNumber(reviewLimit)} du` : ""
  ].filter(Boolean).join(", ");
}

function selectWallGraphRepairCandidate(candidate) {
  const selected = describeWallGraphRepairCandidate(candidate);
  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(candidate.pageNumber ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setStatus(`Selected wall graph repair ${candidate.id || ""}`.trim());
}

function selectWallReliabilityItem(wall) {
  const selected = describeItem("wall", wall);
  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(wall.pageNumber ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setStatus(`Selected wall reliability item ${wall.id || ""}`.trim());
}

function benchmarkDraftForDetection(type, item, overrides = {}) {
  const detectorKey = benchmarkDetectorKeyForSelection(type);
  if (!detectorKey) {
    return null;
  }

  const bounds = normalizeRect(overrides.bounds ?? item.bounds ?? item.representativeBounds)
    ?? boundsFromDetectionGeometry(item);
  const pageNumber = normalizedPageNumber(overrides.pageNumber ?? item.pageNumber ?? firstObjectGroupPage(item));
  const target = cleanBenchmarkTarget({
    pageNumber,
    bounds,
    label: overrides.label ?? item.label ?? item.name ?? item.candidateLabel,
    text: overrides.text ?? item.text ?? item.symbolName ?? item.suppressedByAggregateId,
    marker: overrides.marker ?? item.marker,
    regionKind: detectorKey === "regionMetrics" ? item.kind : null,
    dimensionKind: detectorKey === "dimensionMetrics" ? item.kind : null,
    dimensionOrientation: detectorKey === "dimensionMetrics" ? item.orientation : null,
    annotationKind: detectorKey === "annotationMetrics" || detectorKey === "annotationReferenceMetrics"
      ? (overrides.annotationKind ?? item.kind)
      : null,
    gridAxisOrientation: detectorKey === "gridAxisMetrics" ? item.orientation : null,
    openingType: detectorKey === "openingMetrics" ? item.type : null,
    openingOperation: detectorKey === "openingMetrics" ? item.operation : null,
    objectCategory: detectorKey === "objectMetrics"
      || detectorKey === "objectGroupMetrics"
      || detectorKey === "objectAggregateMetrics"
      || detectorKey === "routingObstacleMetrics"
        ? item.category
      : detectorKey === "routingSuppressedObjectMetrics"
        ? item.candidateCategory
        : null,
    objectKind: detectorKey === "objectMetrics" || detectorKey === "objectGroupMetrics" || detectorKey === "objectAggregateMetrics" ? item.kind
      : detectorKey === "routingObstacleMetrics" ? item.objectKind
        : detectorKey === "routingSuppressedObjectMetrics" ? item.candidateKind
        : null,
    routingSourceKind: detectorKey.startsWith("routing") ? item.sourceKind : null,
    routingObstacleKind: detectorKey === "routingObstacleMetrics" ? item.obstacleKind : null,
    routingInfluence: detectorKey === "objectAggregateMetrics" || detectorKey === "routingObstacleMetrics"
      ? item.routingInfluence
      : detectorKey === "routingSuppressedObjectMetrics" ? item.aggregateRoutingInfluence
      : null,
    structuralInfluence: detectorKey === "objectAggregateMetrics" || detectorKey === "routingObstacleMetrics"
      ? item.structuralInfluence
      : detectorKey === "routingSuppressedObjectMetrics" ? item.aggregateStructuralInfluence
      : null,
    roomUseKind: detectorKey === "objectAggregateMetrics" ? item.roomUseEvidence
      : detectorKey === "routingRoomUseHintMetrics" ? item.roomUseKind
        : null,
    suppressesChildObjects: detectorKey === "objectAggregateMetrics" ? item.suppressChildObjectsForRouting
      : detectorKey === "routingObstacleMetrics" ? item.suppressesChildObjects
        : null,
    minCount: detectorKey === "objectGroupMetrics" ? item.count
      : detectorKey === "objectAggregateMetrics" ? item.childObjectCount ?? item.childObjectIds?.length
        : detectorKey === "routingObstacleMetrics" && item.childObjectIds?.length ? item.childObjectIds.length
          : null,
    requiresReview: detectorKey === "objectGroupMetrics" || detectorKey === "objectAggregateMetrics" ? item.requiresReview : null,
    objectCandidateId: detectorKey === "routingSuppressedObjectMetrics" ? item.objectCandidateId : null,
    suppressedByAggregateId: detectorKey === "routingSuppressedObjectMetrics" ? item.suppressedByAggregateId : null,
    suppressionReason: detectorKey === "routingSuppressedObjectMetrics" ? item.reason : null,
    suppressionAction: detectorKey === "routingSuppressedObjectMetrics" ? item.action : null,
    replacementRoutingObstacleId: detectorKey === "routingSuppressedObjectMetrics" ? item.replacementRoutingObstacleId : null,
    roomUseHintId: detectorKey === "routingSuppressedObjectMetrics" ? item.roomUseHintId : null,
    detectedTags: detectorKey === "objectMetrics" || detectorKey === "objectGroupMetrics"
      ? benchmarkDetectedTagsForSelection(item)
      : [],
    confidence: roundConfidence(item.confidence),
    sourcePrimitiveIds: item.sourcePrimitiveIds || item.labelSourcePrimitiveIds || [],
    sourceLayers: item.sourceLayers || [],
    evidence: item.evidence || []
  });

  return {
    detectorKey,
    detectorLabel: benchmarkDetectorLabel(detectorKey),
    target
  };
}

function benchmarkDetectedTagsForSelection(item) {
  const values = [];
  if (item.detectedTag) {
    values.push(item.detectedTag);
  }

  if (Array.isArray(item.detectedTags)) {
    values.push(...item.detectedTags);
  }

  const seen = new Set();
  return normalizeStringArray(values).filter((tag) => {
    const key = tag.toLowerCase();
    if (seen.has(key)) {
      return false;
    }

    seen.add(key);
    return true;
  });
}

function benchmarkDetectorKeyForSelection(type) {
  switch (type) {
    case "region":
      return "regionMetrics";
    case "dimension":
      return "dimensionMetrics";
    case "annotation":
      return "annotationMetrics";
    case "annotation reference":
      return "annotationReferenceMetrics";
    case "grid axis":
      return "gridAxisMetrics";
    case "wall":
      return "wallMetrics";
    case "room":
      return "roomMetrics";
    case "opening":
      return "openingMetrics";
    case "object":
      return "objectMetrics";
    case "object group":
      return "objectGroupMetrics";
    case "object aggregate":
      return "objectAggregateMetrics";
    case "routing barrier":
      return "routingBarrierMetrics";
    case "routing passage":
      return "routingPassageMetrics";
    case "routing obstacle":
      return "routingObstacleMetrics";
    case "routing room-use hint":
      return "routingRoomUseHintMetrics";
    case "routing suppressed object":
      return "routingSuppressedObjectMetrics";
    default:
      return "";
  }
}

function benchmarkDetectorLabel(detectorKey) {
  return benchmarkMetricDescriptors.find((descriptor) => descriptor.key === detectorKey)?.label || "Benchmark targets";
}

function boundsFromDetectionGeometry(item) {
  return boundsFromLine(item.centerLine ?? item.line ?? item.dimensionLine)
    ?? (item.boundary?.length ? boundsFromPoints(item.boundary) : null);
}

function boundsFromLine(line) {
  if (!line?.start || !line?.end) {
    return null;
  }

  const minX = Math.min(Number(line.start.x), Number(line.end.x));
  const minY = Math.min(Number(line.start.y), Number(line.end.y));
  const maxX = Math.max(Number(line.start.x), Number(line.end.x));
  const maxY = Math.max(Number(line.start.y), Number(line.end.y));
  if (![minX, minY, maxX, maxY].every(Number.isFinite)) {
    return null;
  }

  return {
    x: minX,
    y: minY,
    width: Math.max(1, maxX - minX),
    height: Math.max(1, maxY - minY)
  };
}

function describeObjectGroup(group) {
  const bounds = normalizeRect(group.representativeBounds);
  const label = objectGroupLabel(group);
  const pageNumber = firstObjectGroupPage(group);
  const reviewCropBounds = normalizeRect(group.reviewCropBounds)
    ?? buildReviewCropBounds(bounds, pageNumber ?? 1, state.scan);
  return {
    type: "object group",
    id: group.id,
    kind: label,
    confidence: group.confidence,
    bounds,
    reviewCropBounds,
    sourceLayers: group.sourceLayers || [],
    sourcePrimitiveIds: group.sourcePrimitiveIds || [],
    pageNumber,
    detectedTags: group.detectedTags || [],
    visualAi: group.visualAi || null,
    measurement: bounds
      ? `${formatNumber(bounds.width)} x ${formatNumber(bounds.height)} drawing units`
      : `${group.count ?? 0} candidate${group.count === 1 ? "" : "s"}`,
    evidence: group.evidence || [],
    nearbyText: group.nearbyText || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      group.signature ? `signature ${group.signature}` : "",
      group.count == null ? "" : `${group.count} candidate${group.count === 1 ? "" : "s"}`,
      group.requiresReview ? "review required" : "inventory group",
      group.pageNumbers?.length ? `pages ${group.pageNumbers.join(", ")}` : "",
      group.candidateIds?.length ? `${group.candidateIds.length} candidate ids` : "",
      group.detectedTags?.length ? `detected tags ${group.detectedTags.join(", ")}` : "",
      group.visualAi ? `visual AI ${group.visualAi.label} ${formatPercent(group.visualAi.confidence)} ${group.visualAi.modelName || "model"} ${group.visualAi.modelVersion || ""}`.trim() : "",
      group.visualAi?.cropBounds ? `AI crop ${rectSignature(group.visualAi.cropBounds)}` : "",
      reviewCropBounds ? `review crop ${rectSignature(reviewCropBounds)}` : "",
      group.nearbyText?.length ? `nearby text ${group.nearbyText.map((text) => text.text).slice(0, 4).join(", ")}` : ""
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("object group", group)
  };
}

function describeTitleBlock(titleBlock) {
  return {
    type: "title block",
    id: titleBlock.regionId,
    kind: titleBlock.sheetNumber || titleBlock.sheetTitle || "TitleBlock",
    confidence: titleBlock.confidence,
    sourceLayers: titleBlock.sourceLayers || [],
    sourcePrimitiveIds: titleBlock.sourcePrimitiveIds || [],
    pageNumber: titleBlock.pageNumber,
    measurement: "",
    evidence: (titleBlock.fields || []).flatMap((field) => field.evidence || []).slice(0, 4),
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: titleBlockSummary(titleBlock)
  };
}

function describeDimension(dimension) {
  return {
    type: "dimension",
    id: dimension.id,
    kind: dimension.orientation || dimension.kind || "Dimension",
    confidence: dimension.confidence,
    bounds: normalizeRect(dimension.bounds),
    sourceLayers: dimension.sourceLayers || [],
    sourcePrimitiveIds: dimension.sourcePrimitiveIds || [],
    pageNumber: dimension.pageNumber,
    measurement: describeMeasurement(dimension),
    evidence: dimension.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      dimension.normalizedText || dimension.text || "",
      dimension.millimetersPerDrawingUnit == null ? "" : `${formatNumber(dimension.millimetersPerDrawingUnit)} mm/unit`,
      dimension.drawingLength == null ? "" : `${formatNumber(dimension.drawingLength)} drawing units`
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("dimension", dimension)
  };
}

function describeAnnotation(annotation) {
  const items = annotation.items ?? [];
  const references = annotationReferenceCount({ annotations: [annotation] });
  const preview = items
    .slice(0, 4)
    .map((item) => `${item.marker ? `${item.marker}: ` : ""}${item.text || item.kind}`)
    .join(" | ");

  return {
    type: "annotation",
    id: annotation.id,
    kind: annotation.kind || "Text",
    confidence: annotation.confidence,
    bounds: normalizeRect(annotation.bounds),
    sourceLayers: annotation.sourceLayers || [],
    sourcePrimitiveIds: annotation.sourcePrimitiveIds || [],
    pageNumber: annotation.pageNumber,
    measurement: "",
    evidence: annotation.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      annotation.label || "",
      `${items.length} item${items.length === 1 ? "" : "s"}`,
      references ? `${references} reference${references === 1 ? "" : "s"}` : "",
      preview
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("annotation", annotation)
  };
}

function describeAnnotationReference(annotation, item, reference) {
  const marker = reference.marker || item.marker || "";

  return {
    type: "annotation reference",
    id: reference.id,
    kind: marker ? `${annotation.kind || "Annotation"} marker ${marker}` : annotation.kind || "Annotation",
    confidence: reference.confidence,
    bounds: normalizeRect(reference.bounds),
    sourceLayers: reference.sourceLayers || item.sourceLayers || annotation.sourceLayers || [],
    sourcePrimitiveIds: reference.sourcePrimitiveIds || [],
    pageNumber: item.pageNumber ?? annotation.pageNumber,
    measurement: "",
    scaleGroupId: "",
    evidence: reference.evidence || [],
    hostWallIds: [],
    connectedRoomLinks: [],
    roomId: "",
    roomLabel: "",
    nearbyText: [],
    swing: "",
    metadata: [
      reference.text ? `marker text ${reference.text}` : "",
      item.text ? `annotation item ${item.text}` : "",
      annotation.label || annotation.id || ""
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("annotation reference", reference, {
      annotationKind: annotation.kind,
      label: annotation.label,
      pageNumber: item.pageNumber ?? annotation.pageNumber
    })
  };
}

function describeGridAxis(axis) {
  return {
    type: "grid axis",
    id: axis.id,
    kind: axis.label ? `${axis.orientation} ${axis.label}` : axis.orientation,
    confidence: axis.confidence,
    bounds: normalizeRect(axis.bounds) ?? boundsFromLine(axis.line),
    sourceLayers: axis.sourceLayers || [],
    sourcePrimitiveIds: axis.sourcePrimitiveIds || [],
    pageNumber: axis.pageNumber,
    measurement: axis.line ? `${formatNumber(axis.lineLength ?? lineLength(axis.line))} drawing units` : "",
    evidence: axis.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      axis.label ? `label ${axis.label}` : "unlabeled",
      axis.coordinate == null ? "" : `coord ${formatNumber(axis.coordinate)}`,
      axis.labelSourcePrimitiveIds?.length ? `${axis.labelSourcePrimitiveIds.length} label source` : ""
    ].filter(Boolean).join(" | "),
    benchmarkDraft: benchmarkDraftForDetection("grid axis", axis)
  };
}

function describeGridBaySpacing(bay) {
  const first = bay.firstAxisLabel || bay.firstAxisId;
  const second = bay.secondAxisLabel || bay.secondAxisId;
  return {
    type: "grid bay",
    id: bay.id,
    kind: `${bay.axisOrientation || "Grid"} ${first} to ${second}`,
    confidence: bay.confidence,
    sourceLayers: bay.sourceLayers || [],
    sourcePrimitiveIds: bay.sourcePrimitiveIds || [],
    pageNumber: bay.pageNumber,
    measurement: bay.distanceMeters == null
      ? `${formatNumber(bay.drawingDistance)} drawing units`
      : `${formatNumber(bay.distanceMeters)} m`,
    scaleGroupId: bay.measurementScaleGroupId || "",
    evidence: bay.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      `axes ${first} -> ${second}`,
      bay.drawingDistance == null ? "" : `${formatNumber(bay.drawingDistance)} drawing units`,
      bay.sourceRegionId ? `region ${bay.sourceRegionId}` : ""
    ].filter(Boolean).join(" | ")
  };
}

function describeRoomAdjacency(edge) {
  return {
    type: "room link",
    id: edge.id,
    kind: edge.kind || "Adjacency",
    confidence: edge.confidence,
    sourceLayers: [],
    sourcePrimitiveIds: [],
    pageNumber: edge.pageNumber,
    measurement: edge.sharedBoundaryLength == null ? "" : `${formatNumber(edge.sharedBoundaryLength)} drawing units`,
    evidence: edge.evidence || [],
    hostWallIds: edge.sharedWallIds || [],
    roomId: `${edge.firstRoomId} -> ${edge.secondRoomId}`,
    roomLabel: [edge.firstRoomLabel, edge.secondRoomLabel].filter(Boolean).join(" -> "),
    swing: "",
    metadata: [
      edge.directionFromFirstToSecond && edge.directionFromSecondToFirst
        ? `${edge.directionFromFirstToSecond} / ${edge.directionFromSecondToFirst}`
        : "",
      edge.openingIds?.length ? `${edge.openingIds.length} opening(s)` : "",
      edge.sharedWallIds?.length ? `${edge.sharedWallIds.length} shared wall(s)` : ""
    ].filter(Boolean).join(" | ")
  };
}

function describeRoomCluster(cluster) {
  const fallbackKind = `${cluster.roomIds?.length ?? 0} room${cluster.roomIds?.length === 1 ? "" : "s"}`;
  return {
    type: "room cluster",
    id: cluster.id,
    kind: cluster.kind || fallbackKind,
    confidence: cluster.confidence,
    sourceLayers: [],
    sourcePrimitiveIds: [],
    pageNumber: cluster.pageNumber,
    measurement: describeMeasurement(cluster),
    evidence: cluster.evidence || [],
    hostWallIds: cluster.openingIds || [],
    roomId: cluster.roomIds?.join(", ") || "",
    roomLabel: cluster.roomLabels?.join(", ") || "",
    swing: "",
    metadata: [
      fallbackKind,
      cluster.roomAdjacencyIds?.length ? `${cluster.roomAdjacencyIds.length} room link(s)` : "no room links",
      cluster.openingIds?.length ? `${cluster.openingIds.length} opening(s)` : "",
      cluster.areaSquareMeters == null ? "" : `${formatNumber(cluster.areaSquareMeters)} m2`
    ].filter(Boolean).join(" | ")
  };
}

function rectCenter(bounds) {
  return {
    x: (bounds?.x ?? 0) + ((bounds?.width ?? 0) / 2),
    y: (bounds?.y ?? 0) + ((bounds?.height ?? 0) / 2)
  };
}

function gridLabelPoint(axis) {
  return {
    x: axis.line?.start?.x ?? axis.bounds?.x ?? 0,
    y: (axis.line?.start?.y ?? axis.bounds?.y ?? 0) - 6
  };
}

function lineLength(line) {
  if (!line?.start || !line?.end) {
    return 0;
  }

  const dx = line.end.x - line.start.x;
  const dy = line.end.y - line.start.y;
  return Math.sqrt((dx * dx) + (dy * dy));
}

function findTitleBlockForRegion(region) {
  return (state.scan?.titleBlocks ?? []).find((titleBlock) =>
    titleBlock.regionId === region.id
    || (titleBlock.pageNumber === region.pageNumber && rectsMostlyOverlap(titleBlock.bounds, region.bounds)));
}

function rectsMostlyOverlap(first, second) {
  if (!first || !second) {
    return false;
  }

  const left = Math.max(first.x, second.x);
  const top = Math.max(first.y, second.y);
  const right = Math.min(first.x + first.width, second.x + second.width);
  const bottom = Math.min(first.y + first.height, second.y + second.height);
  const overlap = Math.max(0, right - left) * Math.max(0, bottom - top);
  const area = Math.max(1, Math.min(first.width * first.height, second.width * second.height));
  return overlap / area > 0.7;
}

function confidence(value) {
  return Math.max(0.32, Math.min(1, value ?? 0.5));
}

function nodeOpacity(value) {
  return Math.max(0.18, Math.min(0.52, 0.18 + ((value ?? 0.5) * 0.34)));
}

function setSourceLayers(scan = null) {
  elements.sourceLayerList.replaceChildren();
  if (state.kvemo) {
    renderKvemoSourceEvidence(state.kvemo).forEach((item) => elements.sourceLayerList.appendChild(item));
    refreshWorkspaceTabs();
    return;
  }

  if (state.visualSnapshot && !scan) {
    const page = visualSnapshotCurrentPage(state.visualSnapshot);
    const layers = visualSnapshotDensestLayers(page, 24);
    if (!layers.length) {
      elements.sourceLayerList.textContent = "No snapshot layers";
      refreshWorkspaceTabs();
      return;
    }

    layers.forEach((layer) => {
      const wrapper = document.createElement("div");
      wrapper.className = "source-layer";

      const spacer = document.createElement("span");
      spacer.className = "source-layer-spacer";

      const text = document.createElement("span");
      text.innerHTML = `<strong>${escapeHtml(snapshotLayerLabel(layer.name))}</strong><span>${escapeHtml(visualSnapshotLayerMeta(layer))}</span>`;

      const category = document.createElement("em");
      category.textContent = visualSnapshotOverlayKey(layer.name) || "Snapshot";

      wrapper.append(spacer, text, category);
      elements.sourceLayerList.appendChild(wrapper);
    });
    refreshWorkspaceTabs();
    return;
  }

  const layers = scan?.layers ?? [];
  if (!layers.length) {
    elements.sourceLayerList.textContent = "No source layers";
    refreshWorkspaceTabs();
    return;
  }

  layers.forEach((layer) => {
    const key = layerKey(layer.name);
    const wrapper = document.createElement("label");
    wrapper.className = "source-layer";

    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = state.enabledSourceLayers.has(key);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        state.enabledSourceLayers.add(key);
      } else {
        state.enabledSourceLayers.delete(key);
      }
      drawOverlay();
    });

    const text = document.createElement("span");
    const alternatives = (layer.categoryScores ?? [])
      .filter((score) => score.category && score.category !== layer.likelyCategory)
      .slice(0, 2)
      .map((score) => `${score.category} ${Math.round((score.score || 0) * 100)}%`);
    const alternativeText = alternatives.length ? ` - next: ${alternatives.join(", ")}` : "";
    text.innerHTML = `<strong>${escapeHtml(layer.name)}</strong><span>${escapeHtml(layer.sourceFormat || "unknown")} - ${layer.entityCount} primitives - ${Math.round((layer.confidence || 0) * 100)}%${escapeHtml(alternativeText)}</span>`;

    const category = document.createElement("em");
    category.textContent = layer.likelyCategory || "Unknown";
    if (layer.categoryScores?.length) {
      category.title = layer.categoryScores
        .slice(0, 4)
        .map((score) => `${score.category}: ${Math.round((score.score || 0) * 100)}%`)
        .join("\n");
    }

    wrapper.append(checkbox, text, category);
    elements.sourceLayerList.appendChild(wrapper);
  });
  refreshWorkspaceTabs();
}

function setSelection(item = null) {
  elements.selectionDetails.replaceChildren();
  setSelectionCoordinates(item);

  if (!item) {
    elements.selectionDetails.textContent = "Nothing selected";
    refreshWorkspaceTabs();
    return;
  }

  const list = document.createElement("dl");
  const rows = [
    ["Type", item.type],
    ["ID", item.id],
    ["Review key", item.reviewKey || "-"],
    ["Group signature", item.groupSignature || "-"],
    ["Kind", item.kind || "-"],
    ["Diagnostic", item.diagnosticCode || "-"],
    ["Pattern", item.patternSummary || "-"],
    ["Filtered lines", item.filteredLineCount || "-"],
    ["Clusters", item.clusterCount || "-"],
    ["Topology", item.topology || "-"],
    ["Wall component", item.wallComponent || "-"],
    ["Page", item.pageNumber],
    ["Bounds", formatRectCoordinates(item.bounds)],
    ["Center", item.bounds ? formatPointCoordinates(rectCenter(item.bounds)) : "-"],
    ["Line", formatLineCoordinates(item.line)],
    ["Placement", item.placementSummary || "-"],
    ["Metric placement", item.metricPlacementSummary || "-"],
    ["Reliability", item.reliabilitySummary || "-"],
    ["Reliability reasons", item.reliabilityReasons?.length ? item.reliabilityReasons.join(", ") : "-"],
    ["Confidence", item.confidence == null ? "-" : item.confidence.toFixed(2)],
    ["Kvemo", formatVisualAi(item.visualAi)],
    ["Priority", item.reviewPriority || "-"],
    ["Training", item.suggestedTrainingUse || "-"],
    ["Source kind", item.sourceKind || "-"],
    ["Source kind counts", item.sourceKindCounts?.length ? formatKvemoCountSummary(item.sourceKindCounts) : "-"],
    ["Source wall component", item.sourceWallComponentId || "-"],
    ["Source wall IDs", item.sourceWallComponentIds?.length ? item.sourceWallComponentIds.join(", ") : "-"],
    ["Source wall kind", item.sourceWallComponentKind || "-"],
    ["Source wall kind counts", item.sourceWallComponentKindCounts?.length ? formatKvemoCountSummary(item.sourceWallComponentKindCounts) : "-"],
    ["Crop", item.cropBounds ? formatRectCoordinates(item.cropBounds) : "-"],
    ["Image", item.imageFileName || "-"],
    ["Review", item.reviewDecision == null ? "-" : benchmarkTargetDecisionLabel(item.reviewDecision)],
    ["Queue kind", item.reviewQueueKind ? benchmarkReviewQueueKindLabel(item.reviewQueueKind) : "-"],
    ["Action", item.recommendedAction || "-"],
    ["Target added", item.targetAdded ? "Yes" : "-"],
    ["Bounds edit", item.boundsEdited ? "Yes" : "-"],
    ["Measure", item.measurement || "-"],
    ["Scale group", item.scaleGroupId || "-"],
    ["Room", item.roomLabel || item.roomId || "-"],
    ["Metadata", item.metadata || "-"],
    ["Host walls", item.hostWallIds?.length ? item.hostWallIds.join(", ") : "-"],
    ["Swing", item.swing || "-"],
    ["Wall pair", item.pairEvidence ? `separation ${formatNumber(item.pairEvidence.faceSeparation)}, overlap ${formatNumber(item.pairEvidence.overlapRatio)}, score ${formatNumber(item.pairEvidence.score)}` : "-"],
    ["Room links", item.connectedRoomLinks?.length ? item.connectedRoomLinks.map(formatRoomConnectionLink).join("; ") : "-"],
    ["Nearby text", formatNearbyText(item.nearbyText)],
    ["Review reasons", item.reviewReasons?.length ? item.reviewReasons.join(", ") : "-"],
    ["Evidence", item.evidence?.length ? item.evidence.join(", ") : "-"],
    ["Layers", item.sourceLayers?.length ? item.sourceLayers.join(", ") : "-"],
    ["Source count", item.sourcePrimitiveCount ?? item.sourcePrimitiveIds?.length ?? "-"],
    ["Sources", item.sourcePrimitiveIds?.length ? item.sourcePrimitiveIds.slice(0, 4).join(", ") : "-"]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.selectionDetails.appendChild(list);
  if (item.type === "benchmark target" && item.reviewKey) {
    elements.selectionDetails.appendChild(renderBenchmarkSelectionActions(item));
    elements.selectionDetails.appendChild(renderBenchmarkBoundsEditor(item));
  } else if (item.benchmarkDraft && state.benchmarkManifest) {
    elements.selectionDetails.appendChild(renderBenchmarkAuthoringActions(item));
  }
  refreshWorkspaceTabs();
}

function renderBenchmarkAuthoringActions(item) {
  const actions = document.createElement("div");
  actions.className = "benchmark-authoring-actions";

  const add = document.createElement("button");
  add.type = "button";
  add.textContent = `Add ${item.benchmarkDraft.detectorLabel}`;
  add.disabled = !item.benchmarkDraft.target?.bounds;
  add.title = add.disabled
    ? "This detection has no bounds to seed a target."
    : "Add this selected detection as a reviewed benchmark target.";
  add.addEventListener("click", () => addBenchmarkTargetFromSelection(item));

  actions.appendChild(add);
  return actions;
}

function renderBenchmarkSelectionActions(item) {
  const actions = document.createElement("div");
  actions.className = "benchmark-selection-actions";

  [
    ["accepted", "Accept"],
    ["rejected", "Reject"],
    ["needsReview", "Needs review"],
    ["", "Clear"]
  ].forEach(([decision, label]) => {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.className = item.reviewDecision === decision ? "active" : "";
    button.addEventListener("click", () => setBenchmarkReviewDecision(item.reviewKey, decision));
    actions.appendChild(button);
  });

  const deleteButton = document.createElement("button");
  deleteButton.type = "button";
  deleteButton.textContent = item.reviewQueueKind ? "Hide item" : "Delete target";
  deleteButton.className = "danger";
  deleteButton.addEventListener("click", () => deleteBenchmarkTarget(item.reviewKey));
  actions.appendChild(deleteButton);

  return actions;
}

function setBenchmarkReviewDecision(reviewKey, decision) {
  if (!reviewKey) {
    return;
  }

  const normalizedDecision = normalizeBenchmarkReviewDecision(decision);
  if (!normalizedDecision) {
    state.benchmarkReviewDecisions.delete(reviewKey);
  } else {
    state.benchmarkReviewDecisions.set(reviewKey, {
      decision: normalizedDecision,
      reviewedAt: new Date().toISOString()
    });
  }

  refreshBenchmarkReviewUi(reviewKey);
}

function addBenchmarkTargetFromSelection(item) {
  if (!state.benchmarkManifest || !item.benchmarkDraft?.target) {
    return;
  }

  const fixtureIndex = 0;
  const fixture = state.benchmarkManifest.fixtures?.[fixtureIndex];
  if (!fixture) {
    setStatus("Load a benchmark manifest first");
    return;
  }

  const detectorKey = item.benchmarkDraft.detectorKey;
  const descriptor = benchmarkMetricDescriptors.find((entry) => entry.key === detectorKey);
  if (!descriptor) {
    setStatus("Unsupported target type");
    return;
  }

  const sequence = state.benchmarkAddedTargetSequence++;
  const id = uniqueBenchmarkTargetId(detectorKey, item.id || item.kind || "target");
  const target = {
    ...clonePlain(item.benchmarkDraft.target),
    id
  };
  const reviewKey = `added|${fixture.id || "fixture-1"}|${detectorKey}|${id}|${sequence}`;
  const now = new Date().toISOString();
  const normalized = normalizeBenchmarkTargetForState(target, {
    reviewKey,
    detectorKey,
    detectorLabel: descriptor.label,
    targetIndex: Number.MAX_SAFE_INTEGER - sequence,
    fixtureIndex,
    fixtureId: fixture.id || "fixture-1",
    fixtureName: fixture.name || fixture.id || "fixture-1",
    sourcePath: fixture.sourcePath || "",
    isAdded: true,
    addedAt: now
  });

  state.benchmarkTargets.push(normalized);
  state.benchmarkReviewDecisions.set(reviewKey, { decision: "accepted", reviewedAt: now, createdAt: now });
  state.selectedItem = describeBenchmarkTarget(normalized);
  setStatus("Benchmark target added");
  refreshBenchmarkReviewUi(reviewKey);
}

function deleteBenchmarkTarget(reviewKey) {
  if (!reviewKey) {
    return;
  }

  const target = state.benchmarkTargets.find((item) => item.reviewKey === reviewKey);
  if (!target) {
    return;
  }

  if (target.isAdded) {
    state.benchmarkTargets = state.benchmarkTargets.filter((item) => item.reviewKey !== reviewKey);
  } else {
    state.benchmarkDeletedTargets.add(reviewKey);
  }

  state.benchmarkReviewDecisions.delete(reviewKey);
  state.benchmarkTargetEdits.delete(reviewKey);
  state.selectedItem = null;
  setStatus(target.isReviewQueueItem ? "Benchmark queue item hidden" : "Benchmark target deleted");
  refreshBenchmarkReviewUi();
}

function uniqueBenchmarkTargetId(detectorKey, sourceId) {
  const prefix = detectorKey.replace(/Metrics$/i, "").replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase();
  const base = `${prefix}-${String(sourceId || "target").replace(/[^a-z0-9_-]+/gi, "-").replace(/^-+|-+$/g, "") || "target"}`;
  const existing = new Set(state.benchmarkTargets.map((target) => String(target.id || "").toLowerCase()));
  let candidate = base;
  let index = 2;
  while (existing.has(candidate.toLowerCase())) {
    candidate = `${base}-${index}`;
    index++;
  }

  return candidate;
}

function refreshBenchmarkReviewUi(selectedReviewKey = null) {
  setBenchmarkDetails();
  drawOverlay();

  const reviewKey = selectedReviewKey ?? state.selectedItem?.reviewKey;
  if (reviewKey && state.selectedItem?.reviewKey === reviewKey) {
    const target = state.benchmarkTargets.find((item) => item.reviewKey === reviewKey);
    state.selectedItem = target ? describeBenchmarkTarget(target) : null;
  }

  setSelection(state.selectedItem);
  updateNavigation();
}

function renderBenchmarkBoundsEditor(item) {
  const target = state.benchmarkTargets.find((candidate) => candidate.reviewKey === item.reviewKey);
  const editor = document.createElement("div");
  editor.className = "benchmark-bounds-editor";

  const bounds = target?.bounds ?? { x: 0, y: 0, width: 100, height: 100 };
  const fields = [
    ["page", "Page", target?.pageNumber ?? state.currentPage],
    ["x", "X", bounds.x ?? 0],
    ["y", "Y", bounds.y ?? 0],
    ["width", "W", bounds.width ?? 100],
    ["height", "H", bounds.height ?? 100]
  ];
  const inputs = {};

  fields.forEach(([key, label, value]) => {
    const wrapper = document.createElement("label");
    const caption = document.createElement("span");
    const input = document.createElement("input");
    caption.textContent = label;
    input.type = "number";
    input.step = key === "page" ? "1" : "0.1";
    input.value = value == null ? "" : String(value);
    inputs[key] = input;
    wrapper.append(caption, input);
    editor.appendChild(wrapper);
  });

  const actions = document.createElement("div");
  actions.className = "benchmark-bounds-actions";

  const apply = document.createElement("button");
  apply.type = "button";
  apply.textContent = "Apply bounds";
  apply.addEventListener("click", () => {
    const pageNumber = parsePositiveNumber(inputs.page.value, true);
    const rect = {
      x: parseFiniteNumber(inputs.x.value),
      y: parseFiniteNumber(inputs.y.value),
      width: parsePositiveNumber(inputs.width.value),
      height: parsePositiveNumber(inputs.height.value)
    };

    if (pageNumber == null || Object.values(rect).some((value) => value == null)) {
      setStatus("Invalid bounds");
      return;
    }

    setBenchmarkTargetBounds(item.reviewKey, rect, pageNumber);
  });

  const currentPage = document.createElement("button");
  currentPage.type = "button";
  currentPage.textContent = "Use page";
  currentPage.addEventListener("click", () => {
    inputs.page.value = String(state.currentPage);
  });

  const clear = document.createElement("button");
  clear.type = "button";
  clear.textContent = "Clear bounds";
  clear.addEventListener("click", () => setBenchmarkTargetBounds(item.reviewKey, null, null));

  const revert = document.createElement("button");
  revert.type = "button";
  revert.textContent = "Revert";
  revert.disabled = !benchmarkTargetBoundsEdited(item.reviewKey);
  revert.addEventListener("click", () => resetBenchmarkTargetBounds(item.reviewKey));

  actions.append(apply, currentPage, clear, revert);
  editor.appendChild(actions);
  return editor;
}

function parseFiniteNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function parsePositiveNumber(value, integer = false) {
  const number = parseFiniteNumber(value);
  if (number == null || number <= 0) {
    return null;
  }

  return integer ? Math.floor(number) : number;
}

function setBenchmarkTargetBounds(reviewKey, bounds, pageNumber) {
  const target = state.benchmarkTargets.find((item) => item.reviewKey === reviewKey);
  if (!target) {
    return;
  }

  target.bounds = bounds ? normalizeRect(bounds) : null;
  target.pageNumber = pageNumber == null ? null : normalizedPageNumber(pageNumber);

  if (targetBoundsMatchOriginal(target)) {
    state.benchmarkTargetEdits.delete(reviewKey);
  } else {
    state.benchmarkTargetEdits.set(reviewKey, {
      bounds: clonePlain(target.bounds),
      pageNumber: target.pageNumber,
      editedAt: new Date().toISOString()
    });
  }

  setStatus("Bounds updated");
  refreshBenchmarkReviewUi(reviewKey);
}

function resetBenchmarkTargetBounds(reviewKey) {
  const target = state.benchmarkTargets.find((item) => item.reviewKey === reviewKey);
  if (!target) {
    return;
  }

  target.bounds = clonePlain(target.originalBounds);
  target.pageNumber = target.originalPageNumber;
  state.benchmarkTargetEdits.delete(reviewKey);
  setStatus("Bounds reverted");
  refreshBenchmarkReviewUi(reviewKey);
}

function resetAllBenchmarkTargetBounds() {
  state.benchmarkTargets.forEach((target) => {
    target.bounds = clonePlain(target.originalBounds);
    target.pageNumber = target.originalPageNumber;
  });
}

function targetBoundsMatchOriginal(target) {
  return (target.pageNumber ?? null) === (target.originalPageNumber ?? null)
    && rectsEqual(target.bounds, target.originalBounds);
}

function rectsEqual(first, second) {
  if (!first && !second) {
    return true;
  }

  if (!first || !second) {
    return false;
  }

  return roundGeometryValue(first.x) === roundGeometryValue(second.x)
    && roundGeometryValue(first.y) === roundGeometryValue(second.y)
    && roundGeometryValue(first.width) === roundGeometryValue(second.width)
    && roundGeometryValue(first.height) === roundGeometryValue(second.height);
}

function formatRoomConnectionLink(link) {
  const label = link.roomLabel || link.roomId;
  const parts = [
    label,
    link.roomUseKind && link.roomUseKind !== "Unknown" ? link.roomUseKind : "",
    link.sharesHostWall ? "host wall" : "",
    link.roomAdjacencyIds?.length ? `adj ${link.roomAdjacencyIds.join(", ")}` : "",
    link.distanceToOpening == null ? "" : `${formatNumber(link.distanceToOpening)} units`,
    link.confidence == null ? "" : `${formatNumber(link.confidence)} conf`
  ];
  return parts.filter(Boolean).join(" | ");
}

function setTitleBlocks(scan = null) {
  elements.titleBlockDetails.replaceChildren();

  const titleBlocks = scan?.titleBlocks ?? [];
  if (!titleBlocks.length) {
    elements.titleBlockDetails.textContent = "No title metadata";
    return;
  }

  const titleBlock = titleBlocks.find((item) => item.pageNumber === state.currentPage) ?? titleBlocks[0];
  const list = document.createElement("dl");
  const rows = [
    ["Sheet", titleBlock.sheetNumber || "-"],
    ["Title", titleBlock.sheetTitle || "-"],
    ["Project", titleBlock.projectName || "-"],
    ["Revision", titleBlock.revision || "-"],
    ["Date", titleBlock.issueDate || "-"],
    ["Scale", titleBlock.scale || "-"],
    ["Confidence", titleBlock.confidence == null ? "-" : titleBlock.confidence.toFixed(2)],
    ["Fields", `${titleBlock.fields?.length ?? 0}`]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.titleBlockDetails.appendChild(list);

  const fields = titleBlock.fields ?? [];
  if (fields.length) {
    const fieldList = document.createElement("div");
    fieldList.className = "title-field-list";
    fields.slice(0, 8).forEach((field) => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "title-field";
      item.textContent = `${fieldLabel(field.kind)}: ${field.value}`;
      item.title = `Confidence ${Number(field.confidence ?? 0).toFixed(2)}`;
      item.addEventListener("click", () => {
        state.selectedItem = describeTitleField(field);
        setSelection(state.selectedItem);
      });
      fieldList.appendChild(item);
    });
    elements.titleBlockDetails.appendChild(fieldList);
  }
}

function describeTitleField(field) {
  return {
    type: "title field",
    id: field.kind,
    kind: fieldLabel(field.kind),
    confidence: field.confidence,
    sourceLayers: field.sourceLayers || [],
    sourcePrimitiveIds: field.sourcePrimitiveIds || [],
    pageNumber: field.pageNumber,
    measurement: "",
    evidence: field.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: field.value
  };
}

function titleBlockSummary(titleBlock) {
  return [
    titleBlock.sheetNumber ? `sheet ${titleBlock.sheetNumber}` : "",
    titleBlock.sheetTitle || "",
    titleBlock.projectName ? `project ${titleBlock.projectName}` : "",
    titleBlock.revision ? `rev ${titleBlock.revision}` : "",
    titleBlock.issueDate ? `date ${titleBlock.issueDate}` : "",
    titleBlock.scale ? `scale ${titleBlock.scale}` : ""
  ].filter(Boolean).join(" | ");
}

function fieldLabel(kind) {
  return String(kind || "Field")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, (value) => value.toUpperCase());
}

function setCalibration(calibration = null, measurementConsistency = null) {
  elements.calibrationDetails.replaceChildren();

  if (!calibration && !measurementConsistency) {
    elements.calibrationDetails.textContent = "No calibration";
    return;
  }

  const list = document.createElement("dl");
  const rows = [
    ["Status", calibration?.hasReliableMeasurementScale ? "Measured" : "Uncalibrated"],
    ["Unit", calibration?.millimetersPerDrawingUnit == null ? "-" : `${formatNumber(calibration.millimetersPerDrawingUnit)} mm/unit`],
    ["Scale", calibration?.scaleRatio == null ? "-" : `1:${formatNumber(calibration.scaleRatio)}`],
    ["Confidence", calibration?.confidence == null ? "-" : calibration.confidence.toFixed(2)],
    ["Scale groups", `${calibration?.scaleGroups?.length ?? 0}`],
    ["Evidence", `${calibration?.evidence?.length ?? 0}`],
    ["Dim QA", measurementConsistency ? `${measurementConsistency.consistentCount ?? 0}/${measurementConsistency.checkedCount ?? 0} ok` : "-"],
    ["Outliers", measurementConsistency ? `${measurementConsistency.outlierCount ?? 0}` : "-"],
    ["Outlier ratio", measurementConsistency ? formatPercent(measurementOutlierRatio(measurementConsistency)) : "-"],
    ["Selected scale", measurementConsistency?.selectedMillimetersPerDrawingUnit == null ? "-" : `${formatNumber(measurementConsistency.selectedMillimetersPerDrawingUnit)} mm/unit`],
    ["Dim median", measurementConsistency?.medianDimensionMillimetersPerDrawingUnit == null ? "-" : `${formatNumber(measurementConsistency.medianDimensionMillimetersPerDrawingUnit)} mm/unit`],
    ["Dim spread", measurementConsistency?.dimensionScaleSpreadRatio == null ? "-" : `${formatNumber(measurementConsistency.dimensionScaleSpreadRatio)}x`],
    ["Dim conf", measurementConsistency?.confidence == null ? "-" : Number(measurementConsistency.confidence).toFixed(2)]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.calibrationDetails.appendChild(list);

  if (measurementConsistency) {
    elements.calibrationDetails.appendChild(renderMeasurementConsistencyDetails(measurementConsistency));
  }

  const firstScaleGroup = calibration?.scaleGroups?.[0];
  if (firstScaleGroup) {
    const note = document.createElement("p");
    const unit = firstScaleGroup.millimetersPerDrawingUnit == null
      ? ""
      : `, ${formatNumber(firstScaleGroup.millimetersPerDrawingUnit)} mm/unit`;
    const sourceRegion = firstScaleGroup.sourceRegionIds?.length
      ? `, regions ${firstScaleGroup.sourceRegionIds.join(", ")}`
      : "";
    const bounds = firstScaleGroup.bounds
      ? `, bounds ${formatNumber(firstScaleGroup.bounds.x)},${formatNumber(firstScaleGroup.bounds.y)} ${formatNumber(firstScaleGroup.bounds.width)}x${formatNumber(firstScaleGroup.bounds.height)}`
      : "";
    note.textContent = `${firstScaleGroup.scope || "Scale"} evidence on ${firstScaleGroup.pageNumber == null ? "document" : `page ${firstScaleGroup.pageNumber}`}${unit}${sourceRegion}${bounds}.`;
    elements.calibrationDetails.appendChild(note);
    return;
  }

  const firstEvidence = calibration?.evidence?.[0];
  if (firstEvidence) {
    const note = document.createElement("p");
    note.textContent = firstEvidence.description || firstEvidence.text || "";
    elements.calibrationDetails.appendChild(note);
    return;
  }

  const firstCheck = measurementConsistency?.checks?.[0];
  if (firstCheck?.evidence?.length) {
    const note = document.createElement("p");
    note.textContent = firstCheck.evidence[0];
    elements.calibrationDetails.appendChild(note);
  }
}

function renderMeasurementConsistencyDetails(report) {
  const wrapper = document.createElement("div");
  wrapper.className = "measurement-qa";

  const status = document.createElement("div");
  status.className = `measurement-qa-status ${measurementQaStatusClass(report)}`;
  const checkedCount = report.checkedCount ?? 0;
  const consistentCount = report.consistentCount ?? 0;
  const outlierCount = report.outlierCount ?? 0;
  const spread = report.dimensionScaleSpreadRatio == null
    ? ""
    : `, spread ${formatNumber(report.dimensionScaleSpreadRatio)}x`;
  status.textContent = checkedCount
    ? `${consistentCount}/${checkedCount} dimensions agree, ${outlierCount} outlier${outlierCount === 1 ? "" : "s"}${spread}`
    : "No dimension checks available";
  wrapper.appendChild(status);

  const checks = Array.isArray(report.checks) ? report.checks : [];
  const reviewChecks = checks
    .slice()
    .sort(compareMeasurementChecksForReview)
    .slice(0, 6);

  if (reviewChecks.length) {
    const heading = document.createElement("strong");
    heading.className = "measurement-qa-heading";
    heading.textContent = "Dimension checks to review";
    wrapper.appendChild(heading);

    const list = document.createElement("div");
    list.className = "measurement-check-list";
    reviewChecks.forEach((check) => list.appendChild(renderMeasurementCheck(check)));
    wrapper.appendChild(list);
  }

  return wrapper;
}

function renderMeasurementCheck(check) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `measurement-check ${measurementCheckStatusClass(check.status)}`;
  button.title = "Select this dimension check";
  button.addEventListener("click", () => selectMeasurementCheck(check));

  const heading = document.createElement("strong");
  heading.textContent = `${check.status || "Check"} - ${check.dimensionId || "dimension"}`;

  const relativeError = check.relativeError == null ? "-" : formatPercent(Math.abs(Number(check.relativeError)));
  const delta = check.deltaMillimeters == null ? "-" : `${formatSigned(check.deltaMillimeters, 1)} mm`;
  const expected = check.expectedMillimeters == null ? "-" : `${formatNumber(check.expectedMillimeters)} mm`;
  const meta = document.createElement("span");
  meta.textContent = [
    `page ${check.pageNumber ?? "-"}`,
    `${formatNumber(check.dimensionMillimeters)} mm noted`,
    `expected ${expected}`,
    `delta ${delta}`,
    `error ${relativeError}`,
    `${formatNumber(check.impliedMillimetersPerDrawingUnit)} mm/unit`
  ].filter(Boolean).join(" | ");

  const evidence = document.createElement("small");
  const evidenceParts = [
    check.sourceLayers?.length ? `layers ${check.sourceLayers.slice(0, 3).join(", ")}` : "",
    check.sourcePrimitiveIds?.length ? `${check.sourcePrimitiveIds.length} sources` : "",
    check.evidence?.length ? check.evidence.slice(0, 2).join(" | ") : ""
  ].filter(Boolean);
  evidence.textContent = evidenceParts.length ? evidenceParts.join(" | ") : "No evidence text";

  button.append(heading, meta, evidence);
  return button;
}

function selectMeasurementCheck(check) {
  const dimension = (state.scan?.dimensions ?? []).find((item) => item.id === check.dimensionId);
  const dimensionSelection = dimension ? describeDimension(dimension) : null;
  const selected = dimensionSelection
    ? {
        ...dimensionSelection,
        metadata: [dimensionSelection.metadata, measurementCheckSummary(check)].filter(Boolean).join(" | "),
        evidence: [...(dimension.evidence ?? []), ...(check.evidence ?? [])]
      }
    : describeMeasurementCheck(check);

  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(check.pageNumber ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setBenchmarkDetails();
  setStatus(`Selected measurement check ${check.dimensionId || ""}`.trim());
}

function describeMeasurementCheck(check) {
  return {
    type: "measurement check",
    id: check.dimensionId || "",
    kind: check.status || "Measurement",
    confidence: check.confidence,
    bounds: null,
    sourceLayers: check.sourceLayers || [],
    sourcePrimitiveIds: check.sourcePrimitiveIds || [],
    pageNumber: check.pageNumber,
    measurement: measurementCheckSummary(check),
    scaleGroupId: "",
    evidence: check.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: `${formatNumber(check.impliedMillimetersPerDrawingUnit)} mm/unit implied`
  };
}

function measurementCheckSummary(check) {
  const expected = check.expectedMillimeters == null ? "" : `expected ${formatNumber(check.expectedMillimeters)} mm`;
  const delta = check.deltaMillimeters == null ? "" : `delta ${formatSigned(check.deltaMillimeters, 1)} mm`;
  const error = check.relativeError == null ? "" : `error ${formatPercent(Math.abs(Number(check.relativeError)))}`;
  return [
    `${formatNumber(check.dimensionMillimeters)} mm noted`,
    expected,
    delta,
    error,
    `${formatNumber(check.impliedMillimetersPerDrawingUnit)} mm/unit implied`
  ].filter(Boolean).join(" | ");
}

function measurementOutlierRatio(report) {
  const checkedCount = Number(report?.checkedCount ?? 0);
  if (!Number.isFinite(checkedCount) || checkedCount <= 0) {
    return 0;
  }

  return Number(report?.outlierCount ?? 0) / checkedCount;
}

function measurementQaStatusClass(report) {
  const ratio = measurementOutlierRatio(report);
  const spread = Number(report?.dimensionScaleSpreadRatio ?? 0);
  if (!report?.hasReliableCalibration || ratio >= 0.5 || spread >= 5) {
    return "poor";
  }

  if ((report?.outlierCount ?? 0) > 0 || spread >= 2) {
    return "review";
  }

  return "strong";
}

function compareMeasurementChecksForReview(first, second) {
  return measurementCheckReviewScore(second) - measurementCheckReviewScore(first);
}

function measurementCheckReviewScore(check) {
  const status = String(check?.status || "").toLowerCase();
  const statusScore = status.includes("outlier") ? 1000 : status.includes("consistent") ? 0 : 300;
  const error = Math.abs(Number(check?.relativeError ?? 0));
  const delta = Math.abs(Number(check?.deltaMillimeters ?? 0)) / 1000;
  return statusScore + error + delta;
}

function measurementCheckStatusClass(status) {
  const normalized = String(status || "").toLowerCase();
  if (normalized.includes("outlier")) {
    return "outlier";
  }

  if (normalized.includes("consistent")) {
    return "consistent";
  }

  return "review";
}

function setQuality(quality = null) {
  elements.qualityDetails.replaceChildren();

  if (!quality) {
    elements.qualityDetails.textContent = "No quality report";
    return;
  }

  const list = document.createElement("dl");
  const rows = [
    ["Grade", quality.grade || "-"],
    ["Confidence", quality.overallConfidence == null ? "-" : Number(quality.overallConfidence).toFixed(2)],
    ["Review", quality.requiresReview ? "Yes" : "No"],
    ["Detectors", `${quality.detectorWithFindingsCount ?? 0}/${quality.detectorCount ?? 0}`],
    ["Findings", `${quality.detectionCount ?? 0}`],
    ["Diagnostics", `${quality.diagnosticWarningCount ?? 0} warn / ${quality.diagnosticErrorCount ?? 0} err`],
    ["Scan risks", `${scanRiskIssues(quality).length}`]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  const grade = document.createElement("div");
  grade.className = `quality-grade ${qualityGradeClass(quality.grade)}`;
  grade.textContent = `${quality.grade || "Unknown"} - ${quality.overallConfidence == null ? "-" : Number(quality.overallConfidence).toFixed(2)}`;

  const issueList = document.createElement("div");
  issueList.className = "quality-issues";
  const issues = quality.issues ?? [];
  const riskIssues = scanRiskIssues(quality);
  const ordinaryIssues = issues.filter((issue) => !isScanRiskIssue(issue));
  const riskList = document.createElement("div");
  riskList.className = "quality-risk-list";
  if (riskIssues.length) {
    const riskSummary = document.createElement("div");
    riskSummary.className = "quality-risk-summary";
    riskSummary.textContent = `${riskIssues.length} professional scan-risk issue${riskIssues.length === 1 ? "" : "s"} require review`;
    riskList.appendChild(riskSummary);
    riskIssues.slice(0, 5).forEach((issue) => {
      riskList.appendChild(renderQualityIssue(issue, "quality-risk"));
    });
  }

  if (ordinaryIssues.length) {
    ordinaryIssues.slice(0, 4).forEach((issue) => {
      issueList.appendChild(renderQualityIssue(issue, "quality-issue"));
    });
  }

  const detectorList = document.createElement("div");
  detectorList.className = "quality-detectors";
  const detectors = [...(quality.detectors ?? [])]
    .filter((detector) => (detector.itemCount ?? 0) > 0)
    .sort((first, second) => (second.reviewRequiredCount ?? 0) - (first.reviewRequiredCount ?? 0)
      || (first.averageConfidence ?? 0) - (second.averageConfidence ?? 0))
    .slice(0, 5);

  detectors.forEach((detector) => {
    const item = document.createElement("div");
    item.className = "quality-detector";
    item.textContent = `${detector.name}: ${detector.itemCount ?? 0} @ ${Number(detector.averageConfidence ?? 0).toFixed(2)}`
      + ((detector.reviewRequiredCount ?? 0) > 0 ? `, ${detector.reviewRequiredCount} review` : "");
    detectorList.appendChild(item);
  });

  elements.qualityDetails.append(grade, list);
  if (riskList.childElementCount > 0) {
    elements.qualityDetails.appendChild(riskList);
  }

  if (issueList.childElementCount > 0) {
    elements.qualityDetails.appendChild(issueList);
  }

  if (detectorList.childElementCount > 0) {
    elements.qualityDetails.appendChild(detectorList);
  }
}

function renderQualityIssue(issue, className) {
  const item = document.createElement("div");
  item.className = `${className} ${String(issue.severity || "").toLowerCase()}`;

  const heading = document.createElement("strong");
  heading.textContent = issue.code || "quality";

  const message = document.createElement("span");
  message.textContent = issue.message || issue.severity || "";

  item.append(heading, message);

  const properties = issue.properties && typeof issue.properties === "object"
    ? Object.entries(issue.properties)
      .filter(([, value]) => value !== null && value !== undefined && value !== "")
      .slice(0, 4)
    : [];
  if (properties.length) {
    const meta = document.createElement("small");
    meta.textContent = properties.map(([key, value]) => `${key}: ${value}`).join(" / ");
    item.appendChild(meta);
  }

  return item;
}

function qualityIssues(quality = null) {
  return Array.isArray(quality?.issues) ? quality.issues : [];
}

function scanRiskIssues(quality = null) {
  return qualityIssues(quality).filter(isScanRiskIssue);
}

function isScanRiskIssue(issue) {
  return String(issue?.code || "").toLowerCase().startsWith("quality.scan_risk.");
}

function qualityGradeClass(grade) {
  switch (String(grade || "").toLowerCase()) {
    case "strong":
      return "strong";
    case "usable":
      return "usable";
    case "reviewrequired":
      return "review";
    case "poor":
      return "poor";
    default:
      return "unknown";
  }
}

function setCounts(scan = null) {
  const counts = state.kvemo
    ? kvemoCountRows(state.kvemo)
    : state.visualSnapshot && !scan
    ? visualSnapshotCountRows(state.visualSnapshot)
    : state.benchmarkComparison
    ? benchmarkComparisonCountRows(state.benchmarkComparison)
    : state.batchComparison
    ? batchComparisonCountRows(state.batchComparison)
    : state.benchmarkResult && !scan
    ? [
        ["Cases", state.benchmarkResult.caseCount ?? state.benchmarkResult.cases?.length ?? 0],
        ["Failed cases", state.benchmarkResult.failedCaseCount ?? 0],
        ["Assertions failed", state.benchmarkResult.failedAssertionCount ?? 0],
        ["Queue items", activeBenchmarkTargets().length],
        ["Precision extras", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "PrecisionExtra").length],
        ["Spot-check extras", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "SpotCheckExtra").length],
        ["Review-only", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "ReviewOnly").length],
        ["Quality", state.benchmarkResult.scoreboard?.grade ?? "-"]
      ]
    : scan
    ? [
        ["Pages", scan.pages.length],
        ["Regions", scan.regions.length],
        ["Title blocks", scan.titleBlocks?.length ?? 0],
        ["Dimensions", scan.dimensions?.length ?? 0],
        ["Grid axes", scan.gridAxes?.length ?? 0],
        ["Grid bays", scan.gridBaySpacings?.length ?? 0],
        ["Annotations", scan.annotations?.length ?? 0],
        ["Annotation refs", annotationReferenceCount(scan)],
        ["Surface patterns", scan.surfacePatterns?.length ?? 0],
        ["Wall components", scan.wallComponents?.length ?? 0],
        ["Walls", scan.walls.length],
        ["Nodes", scan.nodes.length],
        ["Rooms", scan.rooms.length],
        ["Room links", scan.roomAdjacencyEdges?.length ?? 0],
        ["Room clusters", scan.roomClusters?.length ?? 0],
        ["Openings", scan.openings.length],
        ["Objects", scan.objects.length],
        ["Object groups", scan.objectGroups?.length ?? 0],
        ["Object aggregates", scan.objectAggregates?.length ?? 0],
        ["Routing items", routingLayerItemCount(scan.routingLayer)],
        ["Layers", scan.layers?.length ?? 0],
        ["Quality", scan.quality?.grade ?? "-"],
      ]
    : [
        ["Pages", 0],
        ["Regions", 0],
        ["Title blocks", 0],
        ["Dimensions", 0],
        ["Grid axes", 0],
        ["Grid bays", 0],
        ["Annotations", 0],
        ["Annotation refs", 0],
        ["Surface patterns", 0],
        ["Wall components", 0],
        ["Walls", 0],
        ["Nodes", 0],
        ["Rooms", 0],
        ["Room links", 0],
        ["Room clusters", 0],
        ["Openings", 0],
        ["Objects", 0],
        ["Object groups", 0],
        ["Object aggregates", 0],
        ["Routing items", 0],
        ["Layers", 0],
        ["Quality", "-"],
      ];

  elements.counts.replaceChildren();
  counts.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    elements.counts.append(term, detail);
  });

  syncLayerControls();
  setAnalysisCounts(scan);
  setCoordinateDetails(scan);
  setLegend();
  refreshWorkspaceTabs();
}

function setAnalysisCounts(scan = null) {
  if (!elements.analysisCounts) {
    return;
  }

  const legendItems = overlayLegendItemsForCurrentPayload(scan);
  const visibleLayerCount = legendItems.filter((item) => state.enabledLayers.has(item.key)).length;
  const layerSummary = `${visibleLayerCount} / ${legendItems.length}`;
  const page = scan || state.visualSnapshot ? currentPageDefinition() : null;
  const rows = state.kvemo
    ? kvemoAnalysisRows(state.kvemo)
    : state.visualSnapshot && !scan
    ? visualSnapshotAnalysisRows(state.visualSnapshot, page, layerSummary)
    : state.benchmarkComparison
    ? benchmarkComparisonAnalysisRows(state.benchmarkComparison, visibleLayerCount)
    : state.batchComparison
    ? batchComparisonAnalysisRows(state.batchComparison, visibleLayerCount)
    : state.benchmarkResult && !scan
    ? [
        ["Current page", "-"],
        ["Page size", "-"],
        ["Visible layers", layerSummary],
        ["Visible items", activeBenchmarkTargets().length],
        ["All detections", activeBenchmarkTargets().length],
        ["Source layers", 0],
        ["Diagnostics", state.benchmarkResult.scoreboard?.failureBuckets?.length ?? 0],
        ["Quality", state.benchmarkResult.scoreboard?.grade ?? "-"]
      ]
    : scan
    ? [
        ["Current page", page ? `${page.number}` : "-"],
        ["Page size", page ? `${formatCoordinateNumber(page.width)} x ${formatCoordinateNumber(page.height)}` : "-"],
        ["Visible layers", layerSummary],
        ["Visible items", visibleDetectionCount(scan)],
        ["All detections", totalDetectionCount(scan)],
        ["Source layers", scan.layers?.length ?? 0],
        ["Review queue", scanReviewQueue(scan).length],
        ["Diagnostics", diagnosticCount(scan)],
        ["Quality", scan.quality?.grade ?? "-"]
      ]
    : [
        ["Current page", "-"],
        ["Page size", "-"],
        ["Visible layers", layerSummary],
        ["Visible items", 0],
        ["All detections", 0],
        ["Source layers", 0],
        ["Diagnostics", 0],
        ["Quality", "-"]
      ];

  elements.analysisCounts.replaceChildren();
  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    elements.analysisCounts.append(term, detail);
  });
}

function setLegend() {
  if (!elements.legendList) {
    return;
  }

  elements.legendList.replaceChildren();
  if (state.kvemo) {
    elements.legendList.appendChild(renderKvemoLegend(state.kvemo));
    return;
  }

  if (!state.scan) {
    if (state.visualSnapshot) {
      appendVisualSnapshotLegend(elements.legendList, state.visualSnapshot);
    } else if (state.benchmarkComparison) {
      appendBenchmarkComparisonLegend(elements.legendList);
    } else if (state.batchComparison) {
      appendBatchComparisonLegend(elements.legendList);
    } else if (state.benchmarkResult) {
      appendBenchmarkQueueLegend(elements.legendList);
    } else {
      elements.legendList.textContent = "No scan loaded";
    }
    return;
  }

  const legendItems = overlayLegendItemsForCurrentPayload(state.scan);
  if (!legendItems.length) {
    elements.legendList.textContent = state.placement ? "No placement overlay layers" : "No overlay layers";
    return;
  }

  legendItems.forEach((item) => {
    const row = document.createElement("div");
    const enabled = state.enabledLayers.has(item.key);
    row.className = `legend-row${enabled ? "" : " muted"}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";
    swatch.style.borderColor = item.stroke;
    swatch.style.background = item.fill;
    if (item.dash) {
      swatch.style.backgroundImage = `repeating-linear-gradient(90deg, ${item.stroke} 0 5px, transparent 5px 9px)`;
    }

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = item.label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = enabled
      ? `${layerCountForKey(state.scan, item.key)}`
      : "off";

    row.append(swatch, text, meta);
    elements.legendList.appendChild(row);
  });

  if (state.benchmarkResult) {
    appendBenchmarkQueueLegend(elements.legendList);
  }

  if (state.enabledLayers.has("walls")) {
    appendWallTopologyLegend(elements.legendList, state.scan);
    appendWallReliabilityLegend(elements.legendList, state.scan);
  }

  const reviewQueue = scanReviewQueue(state.scan);
  if (state.enabledLayers.has("reviewQueue") && reviewQueue.length) {
    appendScanReviewQueueLegend(elements.legendList, reviewQueue);
  }
}

function appendVisualSnapshotLegend(container, snapshot = state.visualSnapshot) {
  const page = visualSnapshotCurrentPage(snapshot);
  const layers = visualSnapshotDensestLayers(page, 16);
  if (!layers.length) {
    container.textContent = "No snapshot layers";
    return;
  }

  layers.forEach((layer) => {
    const key = visualSnapshotOverlayKey(layer.name);
    const legendItem = overlayLegendItems.find((item) => item.key === key);
    const enabled = !key || state.enabledLayers.has(key);
    const row = document.createElement("div");
    row.className = `legend-row${enabled ? "" : " muted"}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";
    swatch.style.borderColor = legendItem?.stroke ?? "#5f6b7a";
    swatch.style.background = legendItem?.fill ?? "rgba(95, 107, 122, 0.08)";
    if (legendItem?.dash) {
      swatch.style.backgroundImage = `repeating-linear-gradient(90deg, ${legendItem.stroke} 0 5px, transparent 5px 9px)`;
    }

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = snapshotLayerLabel(layer.name);

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = enabled
      ? `${layer.count} / d ${formatNumber(layer.normalizedDensity, 1)}`
      : "off";

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendWallReliabilityLegend(container, scan = state.scan) {
  const reviewWalls = wallReliabilityReviewWalls(scan);
  const blockedWalls = wallReliabilityBlockedWalls(scan);
  const rows = [
    ["wall-review", "Walls requiring review", reviewWalls],
    ["wall-blocked", "Coordinate-blocked walls", blockedWalls]
  ];

  rows.forEach(([className, label, items]) => {
    if (!items.length) {
      return;
    }

    const currentPageCount = items.filter(onCurrentPage).length;
    const row = document.createElement("div");
    row.className = `legend-row ${className}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = currentPageCount === items.length
      ? items.length
      : `${currentPageCount}/${items.length}`;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendWallTopologyLegend(container, scan = state.scan) {
  const walls = (scan?.walls ?? []).filter(onCurrentPage);
  if (!walls.length) {
    return;
  }

  const structuralWalls = walls.filter(shouldDrawWallAsPlacementWall);
  const hiddenWalls = walls.filter((wall) => !shouldDrawWallAsPlacementWall(wall));
  const exteriorWalls = structuralWalls.filter((wall) => String(wall.wallType || "").toLowerCase() === "exterior");
  const interiorWalls = structuralWalls.filter((wall) => String(wall.wallType || "").toLowerCase() === "interior");
  const unknownWalls = structuralWalls.filter((wall) => {
    const wallType = String(wall.wallType || "").toLowerCase();
    return wallType !== "exterior" && wallType !== "interior";
  });
  const rows = [
    ["wall-exterior", "Exterior placement walls", exteriorWalls.length],
    ["wall-interior", "Interior placement walls", interiorWalls.length],
    ["wall-unknown", "Unclassified placement walls", unknownWalls.length],
    ["wall-excluded", "Hidden wall/detail candidates", hiddenWalls.length]
  ];

  rows.forEach(([className, label, count]) => {
    if (!count) {
      return;
    }

    const row = document.createElement("div");
    row.className = `legend-row ${className}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendBatchComparisonLegend(container) {
  [
    ["Regression", "Regression", state.batchComparison?.regressionCount ?? 0],
    ["Improvement", "Improvement", state.batchComparison?.improvementCount ?? 0],
    ["Info", "Info", state.batchComparison?.infoCount ?? 0]
  ].forEach(([kind, label, count]) => {
    const row = document.createElement("div");
    row.className = `legend-row batch-${kind.toLowerCase()}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendBenchmarkComparisonLegend(container) {
  [
    ["Regression", "Regression", state.benchmarkComparison?.regressionCount ?? 0],
    ["Improvement", "Improvement", state.benchmarkComparison?.improvementCount ?? 0],
    ["Info", "Info", state.benchmarkComparison?.infoCount ?? 0]
  ].forEach(([kind, label, count]) => {
    const row = document.createElement("div");
    row.className = `legend-row batch-${kind.toLowerCase()}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendBenchmarkQueueLegend(container) {
  [
    ["PrecisionExtra", "Precision extra"],
    ["SpotCheckExtra", "Spot-check extra"],
    ["ReviewOnly", "Review-only"]
  ].forEach(([kind, label]) => {
    const row = document.createElement("div");
    row.className = `legend-row ${benchmarkReviewQueueKindClass(kind)}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === kind).length;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendScanReviewQueueLegend(container, queue = scanReviewQueue()) {
  appendScanReviewQueueKindLegend(container, queue);

  [
    ["Error", "Blocking review"],
    ["Warning", "Warning review"],
    ["Info", "Review info"]
  ].forEach(([severity, label]) => {
    const count = queue.filter((item) => String(item.severity || "").toLowerCase() === severity.toLowerCase()).length;
    if (!count) {
      return;
    }

    const row = document.createElement("div");
    row.className = `legend-row scan-review-${severity.toLowerCase()}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function appendScanReviewQueueKindLegend(container, queue = scanReviewQueue()) {
  scanReviewQueueKindDefinitions().forEach(({ key: kind, label }) => {
    const count = queue.filter((item) => scanReviewQueueKindKey(item) === kind).length;
    if (!count) {
      return;
    }

    const row = document.createElement("div");
    row.className = `legend-row scan-review-kind-${kind}`;

    const swatch = document.createElement("span");
    swatch.className = "legend-swatch";

    const text = document.createElement("span");
    text.className = "legend-label";
    text.textContent = label;

    const meta = document.createElement("span");
    meta.className = "legend-meta";
    meta.textContent = count;

    row.append(swatch, text, meta);
    container.appendChild(row);
  });
}

function scanReviewQueue(scan = state.scan) {
  return (Array.isArray(scan?.reviewQueue) ? scan.reviewQueue : [])
    .slice()
    .sort(compareScanReviewQueueItems);
}

function scanReviewQueueForPage(scan = state.scan, pageNumber = state.currentPage) {
  return scanReviewQueue(scan).filter((item) => reviewQueueItemAppliesToPage(item, pageNumber));
}

function compareScanReviewQueueItems(first, second) {
  const priorityDelta = Number(first.priority ?? 999) - Number(second.priority ?? 999);
  if (priorityDelta) {
    return priorityDelta;
  }

  const severityDelta = scanReviewQueueSeverityRank(first.severity) - scanReviewQueueSeverityRank(second.severity);
  if (severityDelta) {
    return severityDelta;
  }

  return String(first.id || "").localeCompare(String(second.id || ""));
}

function scanReviewQueueSeverityRank(severity) {
  switch (String(severity || "").toLowerCase()) {
    case "error":
      return 0;
    case "warning":
      return 1;
    case "info":
      return 2;
    default:
      return 3;
  }
}

function scanReviewQueueKindDefinitions() {
  return [
    { key: "measurement-outlier", label: "Measurement outliers", shortLabel: "measure", rank: 0 },
    { key: "surface-pattern-review", label: "Surface patterns", shortLabel: "SP review", rank: 1 },
    { key: "surface-pattern-wall-overlap-review", label: "Surface/wall overlaps", shortLabel: "SP wall", rank: 2 },
    { key: "suppressed-wall-pattern-review", label: "Suppressed dense detail", shortLabel: "suppressed detail", rank: 3 },
    { key: "wall-graph-gap-review", label: "Wall graph gaps", shortLabel: "gap", rank: 4 },
    { key: "opening-review", label: "Openings", shortLabel: "opening", rank: 5 },
    { key: "object-group-review", label: "Object groups", shortLabel: "object group", rank: 6 },
    { key: "object-aggregate-review", label: "Object aggregates", shortLabel: "aggregate", rank: 7 }
  ];
}

function scanReviewQueueKindDefinition(kind) {
  const key = scanReviewQueueKindKey(kind);
  return scanReviewQueueKindDefinitions().find((item) => item.key === key) ?? null;
}

function scanReviewQueueKindBreakdown(queue = scanReviewQueue(), pageNumber = state.currentPage) {
  const counts = new Map();
  const pageCounts = new Map();
  queue.forEach((item) => {
    const key = scanReviewQueueKindKey(item) || "review";
    counts.set(key, (counts.get(key) ?? 0) + 1);
    if (reviewQueueItemAppliesToPage(item, pageNumber)) {
      pageCounts.set(key, (pageCounts.get(key) ?? 0) + 1);
    }
  });

  return [...counts.entries()]
    .map(([key, count]) => {
      const definition = scanReviewQueueKindDefinition(key);
      return {
        key,
        label: definition?.label ?? scanReviewQueueKindLabel(key),
        count,
        currentPageCount: pageCounts.get(key) ?? 0,
        rank: definition?.rank ?? 99
      };
    })
    .sort((first, second) => first.rank - second.rank || second.count - first.count || first.label.localeCompare(second.label));
}

function reviewQueueItemAppliesToPage(item, pageNumber = state.currentPage) {
  const page = Number(pageNumber);
  if (Number(item.pageNumber) === page) {
    return true;
  }

  return Array.isArray(item.pageNumbers)
    && item.pageNumbers.some((candidate) => Number(candidate) === page);
}

function scanReviewQueueKindLabel(kind) {
  const definition = scanReviewQueueKindDefinition(kind);
  if (definition) {
    return definition.label;
  }

  return String(kind || "Review")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, (value) => value.toUpperCase());
}

function scanReviewQueueClassName(item) {
  const severity = String(item.severity || "").toLowerCase();
  const kind = scanReviewQueueKindKey(item);
  return [
    "scan-review-queue",
    severity ? `scan-review-${severity}` : "",
    kind ? `scan-review-kind-${kind}` : ""
  ].filter(Boolean).join(" ");
}

function scanReviewQueueKindKey(value) {
  const kind = typeof value === "object" && value !== null ? value.kind : value;
  return String(kind || "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "");
}

function scanReviewQueueOverlayLabel(item) {
  const definition = scanReviewQueueKindDefinition(item);
  if (!definition) {
    return "";
  }

  if (definition.key === "wall-graph-gap-review"
      || definition.key === "object-group-review"
      || definition.key === "object-aggregate-review") {
    return "";
  }

  return definition.shortLabel;
}

function scanReviewQueueOpacity(item) {
  const confidence = Number(item.confidence ?? 0.7);
  return Math.max(0.45, Math.min(0.92, 0.52 + (Number.isFinite(confidence) ? confidence : 0.7) * 0.3));
}

function scanReviewQueueListItem(item) {
  const page = item.pageNumber ?? item.pageNumbers?.[0];
  const sourceCount = item.sourcePrimitiveIds?.length ?? item.sourcePrimitiveCount;
  return {
    title: `${scanReviewQueueKindLabel(item.kind)}: ${item.itemId || item.id}`,
    meta: [
      `p${page ?? "-"}`,
      item.severity || "-",
      `priority ${item.priority ?? "-"}`,
      sourceCount == null ? "" : `${sourceCount} sources`,
      item.recommendedAction
    ].filter(Boolean).join(" / "),
    onClick: () => selectScanReviewQueueItem(item)
  };
}

function placementIssues() {
  return (Array.isArray(state.placement?.issues) ? state.placement.issues : [])
    .map((issue, index) => normalizePlacementIssueForViewer(issue, index))
    .sort(comparePlacementIssues);
}

function placementIssuesForPage(pageNumber = state.currentPage) {
  const page = normalizedPageNumber(pageNumber);
  return placementIssues()
    .filter((issue) => !page || !issue.pageNumber || issue.pageNumber === page);
}

function comparePlacementIssues(first, second) {
  const severityDelta = placementIssueSeverityRank(first.severity) - placementIssueSeverityRank(second.severity);
  if (severityDelta) {
    return severityDelta;
  }

  return String(first.itemId || first.id || "").localeCompare(String(second.itemId || second.id || ""));
}

function comparePlacementIssuePaintOrder(first, second) {
  const severityDelta = placementIssueSeverityRank(second.severity) - placementIssueSeverityRank(first.severity);
  if (severityDelta) {
    return severityDelta;
  }

  const areaDelta = placementIssueArea(second) - placementIssueArea(first);
  if (areaDelta) {
    return areaDelta;
  }

  return comparePlacementIssues(first, second);
}

function placementIssueArea(issue) {
  const bounds = normalizeRect(issue.bounds);
  return bounds ? bounds.width * bounds.height : 0;
}

function placementIssueSeverityRank(severity) {
  switch (String(severity || "").toLowerCase()) {
    case "error":
      return 0;
    case "warning":
      return 1;
    default:
      return 2;
  }
}

function normalizePlacementIssueForViewer(issue, index = 0) {
  return {
    ...issue,
    id: issue?.id || issue?.itemId || `placement-issue-${index + 1}`,
    pageNumber: normalizedPageNumber(issue?.pageNumber) ?? 1,
    bounds: normalizeRect(issue?.bounds),
    boundsMillimeters: normalizeRect(issue?.boundsMillimeters),
    confidence: Number.isFinite(Number(issue?.confidence)) ? Number(issue.confidence) : null,
    sourcePrimitiveIds: normalizeStringArray(issue?.sourcePrimitiveIds),
    sourceLayers: normalizeStringArray(issue?.sourceLayers),
    evidence: normalizeStringArray(issue?.evidence),
    properties: issue?.properties ?? {}
  };
}

function placementIssueAuditListItems(pageNumber = state.currentPage, limit = 14) {
  return placementIssuesForPage(pageNumber)
    .filter((issue) => issue.bounds)
    .slice(0, limit)
    .map((issue) => ({
      title: `${issue.code || "placement.issue"}: ${issue.itemId || issue.id}`,
      meta: [
        issue.severity || "-",
        `p${issue.pageNumber ?? "-"}`,
        issue.confidence == null ? "" : `conf ${formatNumber(issue.confidence)}`,
        issue.bounds ? formatRectCoordinates(issue.bounds) : "",
        issue.recommendedAction || issue.message || ""
      ].filter(Boolean).join(" / "),
      onClick: () => selectPlacementIssue(issue)
    }));
}

function selectPlacementIssue(issue) {
  const selected = describePlacementIssue(issue);
  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(issue.pageNumber ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setStatus(`Selected placement issue ${issue.itemId || issue.id || ""}`.trim());
}

function describePlacementIssue(issue) {
  const bounds = normalizeRect(issue.bounds);
  const properties = Object.entries(issue.properties ?? {})
    .slice(0, 10)
    .map(([key, value]) => `${key}=${value}`);
  return {
    type: "placement issue",
    id: issue.itemId || issue.id,
    kind: issue.code || "placement.issue",
    diagnosticCode: issue.code || "",
    confidence: issue.confidence,
    bounds,
    line: null,
    boundary: [],
    pageNumber: issue.pageNumber,
    reviewCropBounds: bounds
      ? buildReviewCropBounds(bounds, issue.pageNumber, state.scan)
      : null,
    reviewPriority: issue.severity || "",
    reviewReasons: issue.evidence || [],
    recommendedAction: issue.recommendedAction || "",
    metricPlacementSummary: issue.boundsMillimeters
      ? `bounds ${formatRectCoordinates(issue.boundsMillimeters)} mm`
      : "",
    sourceLayers: issue.sourceLayers || [],
    sourcePrimitiveIds: issue.sourcePrimitiveIds || [],
    sourcePrimitiveCount: issue.sourcePrimitiveIds?.length ?? null,
    metadata: [
      issue.severity ? `severity ${issue.severity}` : "",
      issue.message,
      issue.itemId ? `item ${issue.itemId}` : "",
      ...properties
    ].filter(Boolean).join(" | "),
    evidence: issue.evidence || [],
    hostWallIds: issue.properties?.wallId ? [issue.properties.wallId] : [],
    roomId: "",
    roomLabel: ""
  };
}

function placementIssueOpacity(issue) {
  const confidence = Number(issue.confidence ?? 0.7);
  return Math.max(0.48, Math.min(0.9, 0.55 + (Number.isFinite(confidence) ? confidence : 0.7) * 0.25));
}

function placementIssueShortLabel(issue) {
  if (String(issue.code || "").includes("surface_pattern_wall_overlap")) {
    return "wall?";
  }

  if (String(issue.code || "").includes("unanchored")) {
    return "anchor?";
  }

  if (String(issue.severity || "").toLowerCase() === "error") {
    return "issue";
  }

  return "";
}

function selectScanReviewQueueItem(item) {
  const selected = describeScanReviewQueueItem(item);
  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(item.pageNumber ?? item.pageNumbers?.[0] ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setStatus(`Selected review item ${item.itemId || item.id || ""}`.trim());
}

function describeScanReviewQueueItem(item) {
  const patternSummary = item.properties?.patterns || "";
  const filteredLineCount = item.properties?.filteredLineCount || "";
  const clusterCount = item.properties?.clusterCount || "";
  const gapKind = item.properties?.gapKind || "";
  const gapDistance = item.properties?.gapDistance || "";
  const gapWallIds = item.properties?.wallIds || "";
  const gapNode = item.properties?.nodeX && item.properties?.nodeY
    ? `node ${item.properties.nodeX},${item.properties.nodeY}`
    : "";
  const gapTarget = item.properties?.targetX && item.properties?.targetY
    ? `target ${item.properties.targetX},${item.properties.targetY}`
    : "";
  const properties = Object.entries(item.properties ?? {})
    .slice(0, 8)
    .map(([key, value]) => `${key}=${value}`);
  const bounds = normalizeRect(item.bounds);
  const sourcePrimitiveCount = item.sourcePrimitiveIds?.length ?? item.sourcePrimitiveCount ?? null;
  return {
    type: "scan review item",
    id: item.id,
    kind: scanReviewQueueKindLabel(item.kind),
    reviewQueueKind: item.kind || "",
    diagnosticCode: item.itemId || item.properties?.diagnosticCode || "",
    patternSummary,
    filteredLineCount,
    clusterCount,
    sourcePrimitiveCount,
    confidence: item.confidence,
    bounds,
    line: null,
    boundary: [],
    reviewCropBounds: bounds
      ? buildReviewCropBounds(bounds, item.pageNumber ?? item.pageNumbers?.[0], state.scan)
      : null,
    sourceLayers: item.sourceLayers || [],
    sourcePrimitiveIds: item.sourcePrimitiveIds || [],
    pageNumber: item.pageNumber ?? item.pageNumbers?.[0],
    reviewPriority: item.priority,
    reviewReasons: item.evidence || [],
    recommendedAction: item.recommendedAction || "",
    measurement: "",
    scaleGroupId: "",
    topology: "",
    wallComponent: "",
    evidence: item.evidence || [],
    hostWallIds: [],
    roomId: "",
    roomLabel: "",
    swing: "",
    metadata: [
      item.detector ? `detector ${item.detector}` : "",
      item.severity ? `severity ${item.severity}` : "",
      item.itemId ? `item ${item.itemId}` : "",
      gapKind ? `gap ${gapKind}` : "",
      gapDistance ? `distance ${gapDistance}` : "",
      gapWallIds ? `walls ${gapWallIds}` : "",
      gapNode,
      gapTarget,
      sourcePrimitiveCount == null ? "" : `${sourcePrimitiveCount} source primitives`,
      item.pageNumbers?.length ? `pages ${item.pageNumbers.join(", ")}` : "",
      ...properties
    ].filter(Boolean).join(" | ")
  };
}

function setCoordinateDetails(scan = state.scan) {
  if (!elements.coordinateDetails) {
    return;
  }

  elements.coordinateDetails.replaceChildren();
  if (state.kvemo) {
    elements.coordinateDetails.appendChild(renderDefinitionList([
      ["Space", "page"],
      ["Origin", "top-left"],
      ["Axes", "right / down"],
      ["Entries", state.kvemo.entries.length],
      ["Pages", uniqueValues(state.kvemo.entries.map((entry) => entry.pageNumber)).length],
      ["Schema", state.kvemo.schemaVersion]
    ]));
    return;
  }

  if (!scan) {
    if (state.benchmarkComparison) {
      elements.coordinateDetails.appendChild(renderDefinitionList([
        ["Space", "benchmark run"],
        ["Origin", "baseline/candidate truth-set results"],
        ["Axes", "-"],
        ["Cases", state.benchmarkComparison.cases.length],
        ["Schema", state.benchmarkComparison.schemaVersion]
      ]));
    } else if (state.batchComparison) {
      elements.coordinateDetails.appendChild(renderDefinitionList([
        ["Space", "batch run"],
        ["Origin", "baseline/candidate artifacts"],
        ["Axes", "-"],
        ["Items", state.batchComparison.items.length],
        ["Schema", state.batchComparison.schemaVersion]
      ]));
    } else if (state.benchmarkResult) {
      elements.coordinateDetails.appendChild(renderDefinitionList([
        ["Space", "source page"],
        ["Origin", "source scan"],
        ["Axes", "source scan"],
        ["Items", activeBenchmarkTargets().length],
        ["Schema", state.benchmarkResult.schemaVersion]
      ]));
    } else if (state.visualSnapshot) {
      elements.coordinateDetails.appendChild(renderDefinitionList(visualSnapshotCoordinateRows(state.visualSnapshot)));
    } else {
      elements.coordinateDetails.textContent = "No page loaded";
    }
    return;
  }

  const coordinateSystem = scan.coordinateSystem ?? normalizeCoordinateSystem(null, scan.pages, scan.calibration);
  const page = currentPageDefinition();
  const frame = page ? coordinateFrameForPage(coordinateSystem, page.number) : null;
  const axes = `${coordinateSystem.xAxisDirection || "Right"} / ${coordinateSystem.yAxisDirection || "Down"}`;
  const rows = [
    ["Space", coordinateSystem.coordinateSpace || "-"],
    ["Origin", coordinateSystem.origin || "-"],
    ["Axes", axes],
    ["Order", coordinateSystem.coordinateOrder || "x,y"],
    ["Unit", coordinateSystem.unit || "drawing-unit"],
    ["Page", page ? `${page.number}` : "-"],
    ["Bounds", frame?.bounds ? formatRectCoordinates(frame.bounds) : "-"],
    ["Size", frame ? `${formatCoordinateNumber(frame.width)} x ${formatCoordinateNumber(frame.height)}` : "-"],
    ["mm/unit", coordinateSystem.millimetersPerDrawingUnit == null ? "-" : formatCoordinateNumber(coordinateSystem.millimetersPerDrawingUnit)],
    ["Normalize", frame?.pageToNormalizedTransform ? formatAffineTransform(frame.pageToNormalizedTransform) : "-"]
  ];
  if (state.placement?.qualityGate) {
    rows.push(
      ["Coord trust", state.placement.qualityGate.coordinateTrust || "-"],
      ["Metric trust", state.placement.qualityGate.metricTrust || "-"],
      ["Placement", state.placement.qualityGate.readyForCoordinatePlacement ? "ready" : "blocked/review"]
    );
  }

  elements.coordinateDetails.appendChild(renderDefinitionList(rows));
  if (coordinateSystem.note) {
    const note = document.createElement("p");
    note.textContent = coordinateSystem.note;
    elements.coordinateDetails.appendChild(note);
  }
}

function setCursorCoordinates(point = null) {
  if (!elements.cursorCoordinates) {
    return;
  }

  elements.cursorCoordinates.replaceChildren();
  if (!point) {
    elements.cursorCoordinates.textContent = "x -, y -";
    return;
  }

  const page = currentPageDefinition();
  const normalized = page && page.width && page.height
    ? { x: point.x / page.width, y: point.y / page.height }
    : null;
  const rows = [
    ["Page", state.currentPage],
    ["Point", formatPointCoordinates(point)],
    ["Normalized", normalized ? formatPointCoordinates(normalized, 4) : "-"],
    ["Real world", formatRealWorldPoint(point)]
  ];
  elements.cursorCoordinates.appendChild(renderDefinitionList(rows));
}

function setSelectionCoordinates(item = null) {
  if (!elements.selectionCoordinates) {
    return;
  }

  elements.selectionCoordinates.replaceChildren();
  if (!item) {
    elements.selectionCoordinates.textContent = "Nothing selected";
    return;
  }

  const bounds = normalizeRect(item.bounds);
  const center = bounds ? rectCenter(bounds) : null;
  const page = pageDefinitionByNumber(item.pageNumber);
  const normalizedCenter = center && page?.width && page?.height
    ? { x: center.x / page.width, y: center.y / page.height }
    : null;
  const normalizedBounds = bounds && page?.width && page?.height
    ? { x: bounds.x / page.width, y: bounds.y / page.height, width: bounds.width / page.width, height: bounds.height / page.height }
    : null;
  const rows = [
    ["Type", item.type || "-"],
    ["Page", item.pageNumber || "-"],
    ["Bounds", bounds ? formatRectCoordinates(bounds) : "-"],
    ["Center", center ? formatPointCoordinates(center) : "-"],
    ["Line", formatLineCoordinates(item.line)],
    ["Boundary", item.boundary?.length ? `${item.boundary.length} points` : "-"],
    ["Review crop", item.reviewCropBounds ? formatRectCoordinates(item.reviewCropBounds) : "-"],
    ["Norm center", normalizedCenter ? formatPointCoordinates(normalizedCenter, 4) : "-"],
    ["Norm bounds", normalizedBounds ? formatRectCoordinates(normalizedBounds, 4) : "-"],
    ["Real center", center ? formatRealWorldPoint(center) : "-"],
    ["Real bounds", bounds ? formatRealWorldRect(bounds) : "-"]
  ];
  elements.selectionCoordinates.appendChild(renderDefinitionList(rows));
}

function renderDefinitionList(rows) {
  const list = document.createElement("dl");
  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value == null || value === "" ? "-" : String(value);
    list.append(term, detail);
  });
  return list;
}

function setWorkspaceTab(tabKey = "visualizer") {
  const key = elements.workspacePanels[tabKey] ? tabKey : "visualizer";
  state.activeWorkspaceTab = key;

  elements.workspaceTabButtons.forEach((button) => {
    const active = button.dataset.workspaceTab === key;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", active ? "true" : "false");
  });

  Object.entries(elements.workspacePanels).forEach(([panelKey, panel]) => {
    if (!panel) {
      return;
    }

    const active = panelKey === key;
    panel.classList.toggle("active", active);
    panel.hidden = !active;
  });

  refreshWorkspaceTabs();
}

function refreshWorkspaceTabs() {
  renderAiTabDetails();
  renderGeneralTabDetails();
  renderPipelineTabDetails();
  renderAdvancedTabDetails();
}

function renderAiTabDetails() {
  const container = elements.aiTabDetails;
  if (!container) {
    return;
  }

  container.replaceChildren();
  if (state.kvemo) {
    const summary = kvemoSummary(state.kvemo.entries);
    container.append(
      renderTabCard("Kvemo", kvemoCountRows(state.kvemo)),
      renderTabCard("Review mix", [
        ["High priority", state.kvemo.entries.filter((entry) => entry.reviewPriority === "High").length],
        ["Medium priority", state.kvemo.entries.filter((entry) => entry.reviewPriority === "Medium").length],
        ["Low priority", state.kvemo.entries.filter((entry) => entry.reviewPriority === "Low").length],
        ["Classified", summary.classified],
        ["Needs label", summary.cropOnly]
      ]),
      renderTabListCard(
        "Visible crops",
        filteredKvemoEntries(state.kvemo).slice(0, 10).map((entry) => ({
          title: entry.label || entry.suggestedLabel || entry.detectionKind || entry.id,
          meta: [entry.reviewPriority, entry.suggestedTrainingUse, entry.pageNumber ? `p${entry.pageNumber}` : ""].filter(Boolean).join(" / ")
        })),
        "No visible crops")
    );
    return;
  }

  if (state.visualSnapshot) {
    const snapshot = state.visualSnapshot;
    const page = visualSnapshotCurrentPage(snapshot);
    container.append(
      renderTabCard("AI / Kvemo", [
        ["Model output", "Not present"],
        ["Snapshot type", "Deterministic visual QA"],
        ["Schema", snapshot.schemaVersion || "-"],
        ["Review queue", snapshot.reviewQueueCount ?? 0],
        ["Page issues", visualSnapshotIssuesForPage(page, snapshot).length]
      ]),
      renderTabListCard(
        "Evidence-heavy layers",
        visualSnapshotDensestLayers(page, 10).map((layer) => ({
          title: snapshotLayerLabel(layer.name),
          meta: visualSnapshotLayerMeta(layer)
        })),
        "No layer evidence")
    );
    return;
  }

  if (state.benchmarkComparison) {
    const comparison = state.benchmarkComparison;
    const topCases = comparison.cases
      .slice()
      .sort(compareBenchmarkComparisonCases)
      .slice(0, 12);
    container.append(
      renderTabCard("Review priority", [
        ["Regressions", comparison.regressionCount],
        ["Improvements", comparison.improvementCount],
        ["Info", comparison.infoCount],
        ["Failed cases", comparison.cases.filter(benchmarkComparisonCaseFailed).length],
        ["Skipped cases", comparison.cases.filter(benchmarkComparisonCaseSkipped).length],
        ["Count-drift cases", comparison.cases.filter((item) => benchmarkComparisonTopDelta(item)).length]
      ]),
      renderTabListCard(
        "Highest priority fixtures",
        topCases.map((item) => ({
          title: benchmarkComparisonCaseName(item),
          meta: [
            benchmarkComparisonCaseSeverity(item),
            item.status,
            benchmarkComparisonPassPair(item),
            benchmarkComparisonTopDelta(item),
            `${item.signals?.length ?? 0} signals`
          ].filter(Boolean).join(" / ")
        })),
        "No comparison cases",
        true)
    );
    return;
  }

  if (state.benchmarkResult) {
    container.append(
      renderTabCard("Benchmark review queue", [
        ["Items", activeBenchmarkTargets().length],
        ["Precision extras", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "PrecisionExtra").length],
        ["Spot-check extras", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "SpotCheckExtra").length],
        ["Review-only", activeBenchmarkTargets().filter((target) => benchmarkReviewQueueKind(target.reviewQueueKind) === "ReviewOnly").length],
        ["Accepted", activeBenchmarkTargets().filter((target) => benchmarkTargetDecision(target) === "accepted").length],
        ["Needs review", activeBenchmarkTargets().filter((target) => benchmarkTargetDecision(target) === "needsReview").length]
      ]),
      renderTabListCard(
        "Queue items",
        filteredBenchmarkTargets(activeBenchmarkTargets()).slice(0, 12).map((target) => ({
          title: `${benchmarkReviewQueueKindLabel(target.reviewQueueKind)}: ${target.id}`,
          meta: [
            target.detectorLabel,
            target.pageNumber ? `p${target.pageNumber}` : "unpaged",
            target.recommendedAction
          ].filter(Boolean).join(" / ")
        })),
        "No queue items",
        true)
    );
    return;
  }

  if (state.batchComparison) {
    const comparison = state.batchComparison;
    const topItems = comparison.items
      .slice()
      .sort(compareBatchComparisonItems)
      .slice(0, 12);
    container.append(
      renderTabCard("Review priority", [
        ["Regressions", comparison.regressionCount],
        ["Improvements", comparison.improvementCount],
        ["Info", comparison.infoCount],
        ["Visual issue delta", formatSigned(comparison.visualIssueDelta)],
        ["Diagnostic error delta", formatSigned(comparison.diagnosticErrorDelta)],
        ["Evidence items", comparison.items.filter(batchComparisonItemHasEvidence).length]
      ]),
      renderTabListCard(
        "Highest priority files",
        topItems.map((item) => ({
          title: batchComparisonItemName(item),
          meta: [
            batchComparisonItemSeverity(item),
            item.status,
            batchComparisonTopDelta(item),
            `${item.signals?.length ?? 0} signals`
          ].filter(Boolean).join(" / ")
        })),
        "No comparison items",
        true)
    );
    return;
  }

  const scan = state.scan;
  const queue = scanReviewQueue(scan);
  if (state.placement) {
    container.append(
      renderTabCard("Placement intelligence", [
        ["Source", "openplantrace.placement.v4"],
        ["Surface patterns", scan?.surfacePatterns?.length ?? 0],
        ["Object aggregates", scan?.objectAggregates?.length ?? 0],
        ["Review aggregates", scan?.objectAggregates?.filter((aggregate) => aggregate.requiresReview).length ?? 0],
        ["Routing obstacles", scan?.routingLayer?.obstacles?.length ?? 0],
        ["Suppressed children", scan?.routingLayer?.suppressedObjects?.length ?? 0],
        ["Ignored routing objects", scan?.routingLayer?.ignoredObjects?.length ?? 0],
        ["Model AI", "not loaded"]
      ]),
      renderTabListCard(
        "Aggregate candidates",
        (scan?.objectAggregates ?? []).slice(0, 12).map((aggregate) => ({
          title: aggregate.label || aggregate.category || aggregate.kind || aggregate.id,
          meta: [
            `${aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0} child`,
            objectAggregateCompositionSummary(aggregate),
            knownValue(aggregate.routingInfluence),
            knownValue(aggregate.structuralInfluence),
            aggregate.requiresReview ? "review" : "ready"
          ].filter(Boolean).join(" / ")
        })),
        "No object aggregates",
        true)
    );
    return;
  }

  container.append(
    renderTabCard("Visual AI", [
      ["Kvemo manifest", "not loaded"],
      ["Object candidates", scan?.objects?.length ?? 0],
      ["Object groups", scan?.objectGroups?.length ?? 0],
      ["Object aggregates", scan?.objectAggregates?.length ?? 0],
      ["Surface patterns", scan?.surfacePatterns?.length ?? 0],
      ["Suppressed children", scan?.routingLayer?.suppressedObjects?.length ?? 0],
      ["Ignored routing objects", scan?.routingLayer?.ignoredObjects?.length ?? 0],
      ["Review groups", scan?.objectGroups?.filter((group) => group.requiresReview).length ?? 0],
      ["Review queue", queue.length]
    ]),
    renderTabListCard(
      "Scan review queue",
      queue.slice(0, 14).map((item) => scanReviewQueueListItem(item)),
      "No scan review queue",
      true),
    renderTabListCard(
      "Object aggregates",
      (scan?.objectAggregates ?? []).slice(0, 10).map((aggregate) => ({
        title: aggregate.label || aggregate.category || aggregate.id,
        meta: [
          `${aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0} child`,
          objectAggregateCompositionSummary(aggregate),
          knownValue(aggregate.routingInfluence),
          knownValue(aggregate.roomUseEvidence)
        ].filter(Boolean).join(" / ")
      })),
      "No object aggregates"),
    renderTabListCard(
      "Surface patterns",
      (scan?.surfacePatterns ?? []).slice(0, 10).map((pattern) => ({
        title: pattern.kind || pattern.id,
        meta: [
          pattern.orientation,
          `${pattern.lineCount ?? pattern.sourcePrimitiveIds?.length ?? 0} lines`,
          pattern.requiresReview ? "review" : "ready"
        ].filter(Boolean).join(" / ")
      })),
      "No surface patterns"),
    renderTabListCard(
      "Suppressed routing children",
      (scan?.routingLayer?.suppressedObjects ?? []).slice(0, 10).map((item) => ({
        title: item.candidateLabel || item.objectCandidateId || item.id,
        meta: [item.action, item.suppressedByAggregateId].filter(Boolean).join(" / ")
      })),
      "No suppressed child objects"),
    renderTabListCard(
      "Ignored routing objects",
      (scan?.routingLayer?.ignoredObjects ?? []).slice(0, 10).map((item) => ({
        title: item.candidateLabel || item.objectCandidateId || item.id,
        meta: [item.reason, item.candidateCategory, item.routingInfluence].filter(Boolean).join(" / ")
      })),
      "No ignored routing objects")
  );
}

function renderGeneralTabDetails() {
  const container = elements.generalTabDetails;
  if (!container) {
    return;
  }

  const scan = state.scan;
  const page = scan ? currentPageDefinition() : null;
  const titleBlock = scan?.titleBlocks?.[0] ?? null;
  const queue = scanReviewQueue(scan);
  container.replaceChildren();
  if (state.benchmarkComparison) {
    const comparison = state.benchmarkComparison;
    container.append(
      renderTabCard("Benchmark comparison", [
        ["Status", comparison.passed ? "PASS" : "REGRESSION"],
        ["Cases", comparison.cases.length],
        ["Matched", comparison.matchedCaseCount],
        ["Added", comparison.addedCaseCount],
        ["Removed", comparison.removedCaseCount],
        ["Failed cases", comparison.cases.filter(benchmarkComparisonCaseFailed).length]
      ]),
      renderTabCard("Signals", [
        ["Regressions", comparison.regressionCount],
        ["Improvements", comparison.improvementCount],
        ["Info", comparison.infoCount],
        ["Skipped cases", comparison.cases.filter(benchmarkComparisonCaseSkipped).length],
        ["Count drifts", comparison.cases.filter((item) => benchmarkComparisonTopDelta(item)).length]
      ]),
      renderTabCard("Runs", [
        ["Baseline", comparison.baselineName || "-"],
        ["Candidate", comparison.candidateName || "-"],
        ["Baseline cases", comparison.baselineCaseCount],
        ["Candidate cases", comparison.candidateCaseCount],
        ["Generated", comparison.generatedAt || "-"],
        ["Schema", comparison.schemaVersion]
      ], true),
      renderTabListCard(
        "Regression signals",
        comparison.signals
          .filter((signal) => signal.severity === "Regression")
          .slice(0, 12)
          .map((signal) => ({
            title: signal.code,
            meta: [signal.fixtureId, signal.message, benchmarkComparisonSignalPair(signal)].filter(Boolean).join(" / ")
          })),
        "No regression signals",
        true)
    );
    return;
  }

  if (state.batchComparison) {
    const comparison = state.batchComparison;
    container.append(
      renderTabCard("Batch comparison", [
        ["Status", comparison.passed ? "PASS" : "REGRESSION"],
        ["Items", comparison.items.length],
        ["Matched", comparison.matchedItemCount],
        ["Added", comparison.addedItemCount],
        ["Removed", comparison.removedItemCount],
        ["Status changes", comparison.statusChangeCount]
      ]),
      renderTabCard("Signals", [
        ["Regressions", comparison.regressionCount],
        ["Improvements", comparison.improvementCount],
        ["Info", comparison.infoCount],
        ["Diagnostic err delta", formatSigned(comparison.diagnosticErrorDelta)],
        ["Visual issue delta", formatSigned(comparison.visualIssueDelta)],
        ["Quality confidence delta", formatSigned(comparison.qualityConfidenceAverageDelta, 3)]
      ]),
      renderTabCard("Runs", [
        ["Baseline", comparison.baselineOutputDirectory || "-"],
        ["Candidate", comparison.candidateOutputDirectory || "-"],
        ["Duration delta", formatMilliseconds(comparison.totalDurationDeltaMilliseconds)],
        ["Generated", comparison.generatedAt || "-"],
        ["Schema", comparison.schemaVersion]
      ], true),
      renderTabListCard(
        "Regression signals",
        comparison.signals
          .filter((signal) => signal.severity === "Regression")
          .slice(0, 12)
          .map((signal) => ({
            title: signal.code,
            meta: [signal.key, signal.message, batchComparisonSignalPair(signal)].filter(Boolean).join(" / ")
          })),
        "No regression signals",
        true)
    );
    return;
  }

  if (state.visualSnapshot) {
    const snapshot = state.visualSnapshot;
    const snapshotPage = visualSnapshotCurrentPage(snapshot);
    const issues = visualSnapshotIssuesForPage(snapshotPage, snapshot);
    container.append(
      renderTabCard("Visual snapshot", [
        ["Document", snapshot.documentId || "-"],
        ["Pages", snapshot.pages.length],
        ["Current page", snapshotPage ? `${snapshotPage.pageNumber}` : "-"],
        ["Schema", snapshot.schemaVersion || "-"],
        ["Scan schema", snapshot.scanSchemaVersion || "-"]
      ]),
      renderTabCard("Quality", [
        ["Grade", snapshot.qualityGrade || "-"],
        ["Confidence", snapshot.qualityConfidence == null ? "-" : formatNumber(snapshot.qualityConfidence, 3)],
        ["Review", snapshot.requiresReview ? "Required" : "No"],
        ["Issues on page", issues.length],
        ["Total issues", visualSnapshotIssues(snapshot).length]
      ]),
      renderTabCard("Current page", [
        ["Page size", snapshotPage ? `${formatCoordinateNumber(snapshotPage.width)} x ${formatCoordinateNumber(snapshotPage.height)}` : "-"],
        ["Detection coverage", snapshotPage?.detectionCoverage == null ? "-" : formatPercent(snapshotPage.detectionCoverage)],
        ["Drawable items", snapshotPage?.drawableItemCount ?? 0],
        ["Primitive count", snapshotPage?.primitiveCount ?? 0],
        ["SVG path", snapshotPage?.svgPath || "-"]
      ]),
      renderTabCard("Counts", visualSnapshotCountRows(snapshot), true)
    );
    return;
  }

  container.append(
    renderTabCard("Document", [
      ["Source", scan?.document?.sourceName ?? scan?.sourceName ?? "-"],
      ["Pages", scan?.pages?.length ?? 0],
      ["Current page", page ? `${page.number}` : "-"],
      ["Page size", page ? `${formatCoordinateNumber(page.width)} x ${formatCoordinateNumber(page.height)}` : "-"],
      ["Schema", scan?.schemaVersion ?? "-"]
    ]),
    renderTabCard("Source readiness", sourceReadinessGeneralRows(scan)),
    renderTabCard("Quality", [
      ["Grade", scan?.quality?.grade ?? "-"],
      ["Confidence", scan?.quality?.overallConfidence == null ? "-" : formatNumber(scan.quality.overallConfidence)],
      ["Issues", scan?.quality?.issues?.length ?? 0],
      ["Review queue", queue.length],
      ["Scan risk", scanRiskIssues(scan?.quality).length],
      ["Diagnostics", diagnosticCount(scan)]
    ]),
    renderTabCard("Scale", [
      ["Unit", scan?.coordinateSystem?.unit ?? scan?.calibration?.sourceUnit ?? "-"],
      ["mm/unit", scan?.coordinateSystem?.millimetersPerDrawingUnit ?? scan?.calibration?.millimetersPerDrawingUnit ?? "-"],
      ["Reliable", scan?.calibration?.hasReliableMeasurementScale ?? scan?.measurementConsistency?.hasReliableCalibration ?? "-"],
      ["Checked dims", scan?.measurementConsistency?.checkedCount ?? 0],
      ["Outliers", scan?.measurementConsistency?.outlierCount ?? 0]
    ]),
    renderTabCard("Title", [
      ["Project", titleBlock?.projectName ?? titleBlock?.fields?.projectName ?? "-"],
      ["Drawing", titleBlock?.drawingTitle ?? titleBlock?.fields?.drawingTitle ?? "-"],
      ["Sheet", titleBlock?.sheetNumber ?? titleBlock?.fields?.sheetNumber ?? "-"],
      ["Scale", titleBlock?.scaleText ?? titleBlock?.fields?.scale ?? "-"]
    ]),
    renderTabCard("Counts", generalCountRows(scan), true)
  );
  if (state.placement?.qualityGate) {
    container.append(renderTabCard("Placement gate", [
      ["Coordinate trust", state.placement.qualityGate.coordinateTrust || "-"],
      ["Metric trust", state.placement.qualityGate.metricTrust || "-"],
      ["Coordinate ready", state.placement.qualityGate.readyForCoordinatePlacement ? "Yes" : "No"],
      ["Metric ready", state.placement.qualityGate.readyForMetricPlacement ? "Yes" : "No"],
      ["Review", state.placement.qualityGate.requiresReview ? "Required" : "No"],
      ["Calibration", state.placement.qualityGate.hasReliableCalibration ? "Reliable" : "Unreliable"]
    ], true));
  }
}

function renderPipelineTabDetails() {
  const container = elements.pipelineTabDetails;
  if (!container) {
    return;
  }

  container.replaceChildren();
  if (state.benchmarkResult) {
    const cases = state.benchmarkResult.cases ?? [];
    const stages = benchmarkStageSummaries(cases);
    const lifecycles = stages.flatMap((stage) => (stage.artifactDeltas ?? []).map((delta) => ({
      ...delta,
      stage: stage.stage || stage.displayName || "stage",
      fixtureId: stage.fixtureId || ""
    })));
    const byKind = lifecycleCountRows(lifecycles);
    container.append(
      renderTabCard("Benchmark pipeline", [
        ["Cases", cases.length],
        ["Stage summaries", stages.length],
        ["Lifecycle rows", lifecycles.length],
        ["Changed rows", lifecycles.filter((item) => item.changed || numericDelta(item) !== 0).length],
        ["Empty declared", lifecycles.filter((item) => item.isEmptyDeclaredOutput).length],
        ["Schema", state.benchmarkResult.schemaVersion || "-"]
      ]),
      renderTabCard("Lifecycle mix", byKind),
      renderTabCard("Stage health", benchmarkStageHealthRows(cases)),
      renderTabListCard(
        "Stage telemetry review",
        benchmarkStageHealthItems(cases),
        "No dependency or contract issues in benchmark stages",
        true),
      renderTabListCard(
        "Largest benchmark stage deltas",
        lifecycles
          .slice()
          .sort(compareArtifactLifecycleRows)
          .slice(0, 24)
          .map((item) => pipelineArtifactLifecycleListItem(item)),
        "No benchmark artifact lifecycle rows",
        true)
    );
    return;
  }

  const scan = state.scan;
  container.append(
    renderTabCard("Pipeline plan", pipelinePlanRows(scan)),
    renderTabCard("Runtime read readiness", pipelineRuntimeReadinessRows(scan)),
    renderTabCard("Artifact lifecycle", pipelineArtifactLifecycleRows(scan)),
    renderTabListCard(
      "Stage lifecycle",
      pipelineStageLifecycleItems(scan),
      "No stage lifecycle telemetry",
      true),
    renderTabListCard(
      "Largest artifact movements",
      pipelineArtifactLifecycleItems(scan),
      "No artifact lifecycle rows",
      true),
    renderTabListCard(
      "Empty declared outputs",
      pipelineEmptyDeclaredOutputItems(scan),
      "No empty declared outputs",
      true),
    renderTabListCard(
      "Stages with empty runtime reads",
      pipelineRuntimeReadinessItems(scan),
      "All declared runtime reads had data",
      true),
    renderTabListCard(
      "Stage contract issues",
      pipelineStageContractIssueItems(scan),
      "All stage changes match declared writes",
      true),
    renderTabListCard(
      "Execution waves",
      pipelineExecutionWaveItems(scan),
      "No execution waves recorded",
      true),
    renderTabCard("Artifact ledger", pipelineArtifactLedgerRows(scan)),
    renderTabListCard(
      "Final artifact inventory",
      pipelineArtifactInventoryItems(scan),
      "No final artifact inventory",
      true),
    renderTabListCard(
      "Pipeline plan issues",
      pipelinePlanIssueItems(scan),
      "No pipeline plan issues",
      true)
  );
}

function renderAdvancedTabDetails() {
  const container = elements.advancedTabDetails;
  if (!container) {
    return;
  }

  const scan = state.scan;
  const queue = scanReviewQueue(scan);
  container.replaceChildren();
  if (state.benchmarkComparison) {
    const comparison = state.benchmarkComparison;
    container.append(
      renderTabCard("Comparison contract", [
        ["Schema", comparison.schemaVersion],
        ["Baseline cases", comparison.baselineCaseCount],
        ["Candidate cases", comparison.candidateCaseCount],
        ["Generated", comparison.generatedAt || "-"]
      ]),
      renderTabCard("Aggregate review", [
        ["Regressions", comparison.regressionCount],
        ["Improvements", comparison.improvementCount],
        ["Info", comparison.infoCount],
        ["Added cases", comparison.addedCaseCount],
        ["Removed cases", comparison.removedCaseCount],
        ["Failed cases", comparison.cases.filter(benchmarkComparisonCaseFailed).length]
      ]),
      renderTabListCard(
        "Key count deltas",
        comparison.cases
          .filter((item) => benchmarkComparisonTopDelta(item))
          .slice(0, 14)
          .map((item) => ({
            title: benchmarkComparisonCaseName(item),
            meta: benchmarkComparisonTopDelta(item)
          })),
        "No count deltas",
        true),
      renderTabCard("Selection", [
        ["Type", state.selectedItem?.type ?? "-"],
        ["ID", state.selectedItem?.id ?? "-"],
        ["Kind", state.selectedItem?.kind ?? "-"],
        ["Metadata", state.selectedItem?.metadata ?? "-"]
      ])
    );
    return;
  }

  if (state.batchComparison) {
    const comparison = state.batchComparison;
    container.append(
      renderTabCard("Comparison contract", [
        ["Schema", comparison.schemaVersion],
        ["Baseline items", comparison.baselineItemCount],
        ["Candidate items", comparison.candidateItemCount],
        ["Evidence items", comparison.items.filter(batchComparisonItemHasEvidence).length],
        ["Generated", comparison.generatedAt || "-"]
      ]),
      renderTabCard("Aggregate deltas", [
        ["Diagnostic errors", formatSigned(comparison.diagnosticErrorDelta)],
        ["Diagnostic warnings", formatSigned(comparison.diagnosticWarningDelta)],
        ["Visual issues", formatSigned(comparison.visualIssueDelta)],
        ["Visual error issues", formatSigned(comparison.visualErrorIssueDelta)],
        ["Quality confidence", formatSigned(comparison.qualityConfidenceAverageDelta, 3)],
        ["Duration", formatMilliseconds(comparison.totalDurationDeltaMilliseconds)]
      ]),
      renderTabListCard(
        "Evidence paths",
        comparison.items
          .filter(batchComparisonItemHasEvidence)
          .slice(0, 14)
          .map((item) => ({
            title: batchComparisonItemName(item),
            meta: batchComparisonEvidenceSummary(item)
          })),
        "No evidence paths",
        true),
      renderTabCard("Selection", [
        ["Type", state.selectedItem?.type ?? "-"],
        ["ID", state.selectedItem?.id ?? "-"],
        ["Kind", state.selectedItem?.kind ?? "-"],
        ["Metadata", state.selectedItem?.metadata ?? "-"]
      ])
    );
    return;
  }

  if (state.visualSnapshot) {
    const snapshot = state.visualSnapshot;
    const snapshotPage = visualSnapshotCurrentPage(snapshot);
    const pageIssues = visualSnapshotIssuesForPage(snapshotPage, snapshot);
    const reviewQueue = snapshotPage?.reviewQueue ?? [];
    container.append(
      renderTabCard("Coordinates", visualSnapshotCoordinateRows(snapshot)),
      renderTabCard("Snapshot contract", [
        ["Schema", snapshot.schemaVersion || "-"],
        ["Scan schema", snapshot.scanSchemaVersion || "-"],
        ["Coordinate space", snapshot.coordinateSpace || "-"],
        ["Unit", snapshot.unit || "-"],
        ["Review queue", snapshot.reviewQueueCount ?? 0],
        ["Requires review", snapshot.requiresReview ? "Yes" : "No"]
      ]),
      renderTabListCard(
        "Densest layers",
        visualSnapshotDensestLayers(snapshotPage, 14).map((layer) => ({
          title: snapshotLayerLabel(layer.name),
          meta: visualSnapshotLayerMeta(layer)
        })),
        "No populated snapshot layers",
        true),
      renderTabListCard(
        "Visual issues",
        pageIssues.slice(0, 14).map((issue) => ({
          title: issue.code || "visual.snapshot.issue",
          meta: [issue.severity || "Info", issue.message || "", issue.pageNumber ? `page ${issue.pageNumber}` : ""].filter(Boolean).join(" / ")
        })),
        "No visual issues on this page",
        true),
      renderTabListCard(
        "Review queue",
        reviewQueue.slice(0, 14).map((item) => ({
          title: item.itemId || item.kind || item.code || "review item",
          meta: [item.kind, item.severity, item.message, item.bounds ? formatRectCoordinates(item.bounds) : ""].filter(Boolean).join(" / ")
        })),
        "No compact review items on this page",
        true)
    );
    return;
  }

  container.append(
    renderTabCard("Coordinates", workspaceCoordinateRows(scan)),
    renderTabCard("Source ingestion", sourceReadinessAdvancedRows(scan)),
    renderTabListCard(
      "Source readiness messages",
      sourceReadinessMessageItems(scan),
      "No source readiness messages",
      true),
    renderTabListCard(
      "Source evidence",
      sourceReadinessEvidenceItems(scan),
      "No source readiness evidence",
      true),
    renderTabCard("Benchmark", [
      ["Manifest", state.benchmarkManifest?.name ?? "-"],
      ["Result", state.benchmarkResult?.name ?? "-"],
      ["Grade", state.benchmarkResult?.scoreboard?.grade ?? "-"],
      ["Ready", state.benchmarkResult?.scoreboard?.readyForDownstreamUse == null ? "-" : (state.benchmarkResult.scoreboard.readyForDownstreamUse ? "Yes" : "No")],
      ["Queue", state.benchmarkResult?.reviewQueueCount ?? activeBenchmarkTargets().filter((target) => target.isReviewQueueItem).length],
      ["Targets", activeBenchmarkTargets().length],
      ["Accepted", activeBenchmarkTargets().filter((target) => benchmarkTargetDecision(target) === "accepted").length],
      ["Rejected", activeBenchmarkTargets().filter((target) => benchmarkTargetDecision(target) === "rejected").length],
      ["Needs review", activeBenchmarkTargets().filter((target) => benchmarkTargetDecision(target) === "needsReview").length]
    ]),
    renderTabCard("Scan review queue", [
      ["Items", queue.length],
      ["Blocking", queue.filter((item) => String(item.severity || "").toLowerCase() === "error").length],
      ["Warnings", queue.filter((item) => String(item.severity || "").toLowerCase() === "warning").length],
      ["Info", queue.filter((item) => String(item.severity || "").toLowerCase() === "info").length],
      ["Current page", scanReviewQueueForPage(scan, state.currentPage).length],
      ["With bounds", queue.filter((item) => item.bounds).length]
    ]),
    renderTabCard("Wall reliability", [
      ["Walls", scan?.walls?.length ?? 0],
      ["Review required", wallReliabilityReviewWalls(scan).length],
      ["Coordinate blocked", wallReliabilityBlockedWalls(scan).length],
      ["Current page review", wallReliabilityCurrentPageWalls(scan).length],
      ["With reasons", [...wallReliabilityReviewWalls(scan), ...wallReliabilityBlockedWalls(scan)].filter((wall) => wallReliabilityReasons(wall).length).length]
    ]),
    renderTabListCard(
      "Review queue by kind",
      scanReviewQueueKindBreakdown(queue).map((item) => ({
        title: item.label,
        meta: `${item.count} total / ${item.currentPageCount} current page`
      })),
      "No scan review kinds"),
    renderTabListCard(
      "Surface pattern audit",
      surfacePatternAuditListItems(scan),
      "No surface patterns",
      true),
    renderTabListCard(
      "Top review items",
      queue.slice(0, 16).map((item) => scanReviewQueueListItem(item)),
      "No scan review items",
      true),
    renderTabListCard(
      "Wall reliability audit",
      wallReliabilityAuditListItems(scan),
      "No walls require reliability review on this page",
      true),
    renderTabListCard(
      "Wall graph repair audit",
      wallGraphRepairAuditListItems(scan),
      "No wall graph repair candidates on this page",
      true),
    renderTabListCard(
      "Placement issue audit",
      placementIssueAuditListItems(state.currentPage),
      "No placement issues with coordinates",
      true),
    renderTabListCard(
      "Source layers",
      (scan?.layers ?? []).slice(0, 12).map((layer) => ({
        title: layer.name || "(unlayered)",
        meta: [layer.likelyCategory, layer.entityCount == null ? "" : `${layer.entityCount} primitives`].filter(Boolean).join(" / ")
      })),
      "No source layers"),
    renderTabListCard(
      "Diagnostics",
      diagnosticItems(scan).slice(0, 12).map((message) => ({
        title: message.code || message.stage || message.severity || "diagnostic",
        meta: [message.severity, message.pageNumber ? `p${message.pageNumber}` : "", message.message].filter(Boolean).join(" / ")
      })),
      "No diagnostics"),
    renderTabCard("Selection", [
      ["Type", state.selectedItem?.type ?? "-"],
      ["ID", state.selectedItem?.id ?? "-"],
      ["Page", state.selectedItem?.pageNumber ?? "-"],
      ["Bounds", state.selectedItem?.bounds ? formatRectCoordinates(state.selectedItem.bounds) : "-"],
      ["Room", state.selectedItem?.roomLabel || state.selectedItem?.roomId || "-"]
    ])
  );
  if (state.placement) {
    container.append(
      renderTabCard("Placement contract", [
        ["Schema", state.placement.schemaVersion || "-"],
        ["Scan schema", state.placement.scanSchemaVersion || "-"],
        ["Generated", state.placement.generatedAt || "-"],
        ["Document", state.placement.document?.sourceName || state.placement.document?.id || "-"],
        ["Issues", state.placement.issues?.length ?? 0]
      ], true),
      renderTabListCard(
        "Placement issues",
        placementIssueAuditListItems(state.currentPage, 16),
        "No placement issues",
        true)
    );
  }
}

function renderTabCard(title, rows, wide = false) {
  const card = document.createElement("section");
  card.className = `tab-card${wide ? " wide" : ""}`;
  const heading = document.createElement("h2");
  heading.textContent = title;
  card.append(heading, renderDefinitionList(rows));
  return card;
}

function renderTabListCard(title, items, emptyText, wide = false) {
  const card = document.createElement("section");
  card.className = `tab-card${wide ? " wide" : ""}`;
  const heading = document.createElement("h2");
  heading.textContent = title;
  card.appendChild(heading);

  if (!items.length) {
    const empty = document.createElement("p");
    empty.className = "tab-empty";
    empty.textContent = emptyText;
    card.appendChild(empty);
    return card;
  }

  const list = document.createElement("ul");
  list.className = "tab-list";
  items.forEach((item) => {
    const row = document.createElement("li");
    if (item.className) {
      row.classList.add(...String(item.className).split(/\s+/).filter(Boolean));
    }
    const strong = document.createElement("strong");
    const meta = document.createElement("small");
    strong.textContent = item.title || "-";
    meta.textContent = item.meta || "-";
    row.append(strong, meta);
    if (typeof item.onClick === "function") {
      row.classList.add("clickable");
      row.tabIndex = 0;
      row.setAttribute("role", "button");
      row.addEventListener("click", item.onClick);
      row.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          item.onClick();
        }
      });
    }
    list.appendChild(row);
  });
  card.appendChild(list);
  return card;
}

function surfacePatternAuditListItems(scan = state.scan) {
  return (scan?.surfacePatterns ?? [])
    .filter((pattern) => !pattern.pageNumber || pattern.pageNumber === state.currentPage)
    .slice()
    .sort((first, second) => {
      const firstReview = first.requiresReview ? 1 : 0;
      const secondReview = second.requiresReview ? 1 : 0;
      return secondReview - firstReview
        || (second.sourcePrimitiveIds?.length ?? second.lineCount ?? 0) - (first.sourcePrimitiveIds?.length ?? first.lineCount ?? 0)
        || String(first.id || "").localeCompare(String(second.id || ""));
    })
    .slice(0, 14)
    .map((pattern) => ({
      title: `${pattern.id || "surface-pattern"} - ${pattern.kind || "Surface pattern"}`,
      meta: [
        pattern.orientation || "",
        surfacePatternCountSummary(pattern),
        surfacePatternSpacingSummary(pattern),
        pattern.bounds ? `bounds ${formatRectCoordinates(pattern.bounds)}` : "",
        pattern.excludedFromWallDetection ? "wall-detection excluded" : "",
        pattern.excludedFromStructuralTopology ? "topology excluded" : "",
        pattern.requiresReview ? "review" : "ready"
      ].filter(Boolean).join(" / "),
      onClick: () => selectSurfacePattern(pattern)
    }));
}

function surfacePatternCountSummary(pattern) {
  const parts = [
    Number.isFinite(Number(pattern.lineCount)) ? `${pattern.lineCount} lines` : "",
    Number.isFinite(Number(pattern.horizontalLineCount)) ? `${pattern.horizontalLineCount} h` : "",
    Number.isFinite(Number(pattern.verticalLineCount)) ? `${pattern.verticalLineCount} v` : "",
    Number.isFinite(Number(pattern.intersectionCount)) ? `${pattern.intersectionCount} intersections` : ""
  ].filter(Boolean);
  return parts.join(", ");
}

function surfacePatternSpacingSummary(pattern) {
  const parts = [
    pattern.medianSpacing != null ? `spacing ${formatNumber(pattern.medianSpacing)}` : "",
    pattern.horizontalMedianSpacing != null ? `h ${formatNumber(pattern.horizontalMedianSpacing)}` : "",
    pattern.verticalMedianSpacing != null ? `v ${formatNumber(pattern.verticalMedianSpacing)}` : ""
  ].filter(Boolean);
  return parts.length ? `${parts.join(", ")} drawing units` : "";
}

function selectSurfacePattern(pattern) {
  const selected = describeSurfacePattern(pattern);
  state.selectedItem = selected;
  const pageNumber = normalizedPageNumber(pattern.pageNumber ?? selected.pageNumber);
  if (pageNumber && pageNumber !== state.currentPage) {
    state.currentPage = pageNumber;
    void renderCurrentPage();
  } else {
    drawOverlay();
  }

  setSelection(selected);
  setStatus(`Selected surface pattern ${pattern.id || ""}`.trim());
}

function generalCountRows(scan) {
  if (!scan) {
    return [
      ["Regions", 0],
      ["Walls", 0],
      ["Surface patterns", 0],
      ["Rooms", 0],
      ["Openings", 0],
      ["Objects", 0],
      ["Routing items", 0]
    ];
  }

  return [
    ["Regions", scan.regions?.length ?? 0],
    ["Walls", scan.walls?.length ?? 0],
    ["Surface patterns", scan.surfacePatterns?.length ?? 0],
    ["Rooms", scan.rooms?.length ?? 0],
    ["Room clusters", scan.roomClusters?.length ?? 0],
    ["Openings", scan.openings?.length ?? 0],
    ["Dimensions", scan.dimensions?.length ?? 0],
    ["Annotations", scan.annotations?.length ?? 0],
    ["Objects", scan.objects?.length ?? 0],
    ["Object groups", scan.objectGroups?.length ?? 0],
    ["Object aggregates", scan.objectAggregates?.length ?? 0],
    ["Review queue", scanReviewQueue(scan).length],
    ["Routing items", routingLayerItemCount(scan.routingLayer)],
    ["Suppressed children", scan.routingLayer?.suppressedObjects?.length ?? 0],
    ["Ignored routing objects", scan.routingLayer?.ignoredObjects?.length ?? 0]
  ];
}

function sourceReadinessGeneralRows(scan) {
  const document = scan?.document ?? {};
  const readiness = document.sourceReadiness ?? {};
  return [
    ["Status", readiness.status || sourceReadinessFallbackStatus(document)],
    ["Geometry basis", readiness.geometryBasis || "-"],
    ["Ingestion", document.ingestionPath || "-"],
    ["Format", document.sourceFormat || document.properties?.format || "-"],
    ["Loader", document.loader || document.properties?.loader || "-"],
    ["Vector geometry", formatNullableBoolean(readiness.canUseVectorGeometry)],
    ["Needs adapter", formatNullableBoolean(readiness.requiresExternalAdapter)],
    ["Needs OCR", formatNullableBoolean(readiness.requiresOcr)]
  ];
}

function sourceReadinessAdvancedRows(scan) {
  const document = scan?.document ?? {};
  const readiness = document.sourceReadiness ?? {};
  return [
    ["Source kind", document.sourceKind || document.properties?.sourceKind || "-"],
    ["Effective kind", document.effectiveSourceKind || document.properties?.effectiveSourceKind || "-"],
    ["Clipboard kind", document.clipboardContentKind || "-"],
    ["Extension", document.fileExtension || "-"],
    ["Content type", document.contentType || "-"],
    ["DWG-derived", formatNullableBoolean(document.isDwgDerived)],
    ["Raster-derived", formatNullableBoolean(document.isRasterDerived)],
    ["Legal adapter", formatNullableBoolean(readiness.isLegalAdapterBacked)]
  ];
}

function sourceReadinessMessageItems(scan) {
  const readiness = scan?.document?.sourceReadiness;
  if (!readiness) {
    return [];
  }

  return (readiness.messages ?? []).map((message, index) => ({
    title: `message ${index + 1}`,
    meta: message
  }));
}

function sourceReadinessEvidenceItems(scan) {
  const readiness = scan?.document?.sourceReadiness;
  if (!readiness) {
    return [];
  }

  return (readiness.evidence ?? []).map((evidence, index) => ({
    title: `evidence ${index + 1}`,
    meta: evidence
  }));
}

function pipelinePlanRows(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  const stages = pipelineStages(scan);
  const issueCounts = pipelineIssueCounts(scan);
  const contractCounts = pipelineContractCounts(scan);
  const maxHardLevel = stages.reduce((max, stage) => Math.max(max, nonNegativeInteger(stage.dependencyLevel)), 0);
  const maxPreferredLevel = stages.reduce((max, stage) => Math.max(max, nonNegativeInteger(stage.preferredDependencyLevel)), 0);
  return [
    ["Execution", diagnostics?.executionModel || "-"],
    ["Stages", diagnostics?.stageCount ?? stages.length],
    ["Execution waves", diagnostics?.executionWaveCount ?? pipelineExecutionWaves(scan).length],
    ["Parallel candidates", diagnostics?.parallelCandidateStageCount ?? "-"],
    ["Dependency ready", formatNullableBoolean(diagnostics?.isDependencyReady)],
    ["Source artifacts", listSummary(diagnostics?.sourceArtifacts, 4)],
    ["Known artifacts", listSummary(diagnostics?.artifactKinds, 4)],
    ["Hard levels", maxHardLevel || "-"],
    ["Preferred levels", maxPreferredLevel || "-"],
    ["Contract clean", contractCounts.violations ? "No" : "Yes"],
    ["Contract violations", contractCounts.violations],
    ["Plan issues", issueCounts.total],
    ["Warnings", issueCounts.warnings],
    ["Errors", issueCounts.errors]
  ];
}

function pipelineRuntimeReadinessRows(scan) {
  const stages = pipelineStages(scan);
  const readiness = stages.map(stageRuntimeReadiness);
  const stagesWithTelemetry = readiness.filter(Boolean);
  const emptyRequiredStages = stagesWithTelemetry.filter((item) => item.hasEmptyRequiredReads);
  const emptyOptionalStages = stagesWithTelemetry.filter((item) => item.hasEmptyOptionalReads);
  const nonEmptyRequiredReads = stagesWithTelemetry.reduce((total, item) => total + arrayCount(item.nonEmptyRequiredReads), 0);
  const emptyRequiredReads = stagesWithTelemetry.reduce((total, item) => total + arrayCount(item.emptyRequiredReads), 0);
  const nonEmptyOptionalReads = stagesWithTelemetry.reduce((total, item) => total + arrayCount(item.nonEmptyOptionalReads), 0);
  const emptyOptionalReads = stagesWithTelemetry.reduce((total, item) => total + arrayCount(item.emptyOptionalReads), 0);
  const noDataStages = stagesWithTelemetry.filter((item) =>
    !arrayCount(item.nonEmptyRequiredReads)
    && arrayCount(item.emptyRequiredReads) > 0);
  return [
    ["Stages with telemetry", `${stagesWithTelemetry.length} / ${stages.length}`],
    ["Stages with empty required", emptyRequiredStages.length],
    ["Stages with empty optional", emptyOptionalStages.length],
    ["Required reads with data", nonEmptyRequiredReads],
    ["Empty required reads", emptyRequiredReads],
    ["Optional reads with data", nonEmptyOptionalReads],
    ["Empty optional reads", emptyOptionalReads],
    ["Stages with no required data", noDataStages.length]
  ];
}

function benchmarkStageSummaries(cases) {
  return (Array.isArray(cases) ? cases : [])
    .flatMap((item, caseIndex) => (Array.isArray(item?.stages) ? item.stages : []).map((stage, stageIndex) => ({
      ...stage,
      fixtureId: stage.fixtureId || item?.fixtureId || `case-${caseIndex + 1}`,
      fixtureName: stage.fixtureName || item?.fixtureName || item?.fixtureId || `case-${caseIndex + 1}`,
      stageIndex
    })));
}

function benchmarkStageHealthRows(cases) {
  const stages = benchmarkStageSummaries(cases);
  const health = stages.map(benchmarkStageHealth);
  return [
    ["Stages", stages.length],
    ["Dependency gaps", health.filter((item) => item.hasDependencyGap).length],
    ["Runtime read gaps", health.filter((item) => item.emptyRequiredReads > 0 || item.emptyOptionalReads > 0).length],
    ["Contract drift", health.filter((item) => item.hasContractDrift).length],
    ["Undeclared changes", health.reduce((total, item) => total + item.undeclaredChangedArtifacts, 0)],
    ["Empty declared outputs", health.reduce((total, item) => total + item.emptyDeclaredOutputs, 0)],
    ["Missing required deps", health.reduce((total, item) => total + item.missingRequiredReads, 0)],
    ["Missing optional deps", health.reduce((total, item) => total + item.missingOptionalReads, 0)]
  ];
}

function benchmarkStageHealthItems(cases) {
  return benchmarkStageSummaries(cases)
    .map((stage) => ({ stage, health: benchmarkStageHealth(stage) }))
    .filter((item) => item.health.hasDependencyGap || item.health.hasContractDrift || item.health.emptyDeclaredOutputs)
    .sort((first, second) =>
      Number(second.health.hasContractDrift) - Number(first.health.hasContractDrift)
      || Number(second.health.hasDependencyGap) - Number(first.health.hasDependencyGap)
      || second.health.emptyDeclaredOutputs - first.health.emptyDeclaredOutputs
      || second.health.undeclaredChangedArtifacts - first.health.undeclaredChangedArtifacts
      || second.health.missingRequiredReads - first.health.missingRequiredReads
      || String(first.stage.fixtureId || "").localeCompare(String(second.stage.fixtureId || ""))
      || String(first.stage.stage || "").localeCompare(String(second.stage.stage || "")))
    .slice(0, 30)
    .map(({ stage, health }) => ({
      title: `${stage.fixtureId || "fixture"} / ${stage.displayName || stage.stage || "stage"}`,
      meta: [
        health.hasContractDrift ? `contract drift ${listSummary(health.undeclaredArtifacts, 4)}` : "contract ok",
        health.hasDependencyGap ? "dependency gap" : "dependencies ready",
        health.emptyRequiredReads ? `empty required ${listSummary(health.emptyRequiredArtifacts, 4)}` : "",
        health.emptyOptionalReads ? `empty optional ${listSummary(health.emptyOptionalArtifacts, 3)}` : "",
        health.emptyDeclaredOutputs ? `empty outputs ${listSummary(health.emptyDeclaredArtifacts, 4)}` : "",
        health.changedArtifacts.length ? `changes ${pipelineArtifactLifecycleSummary(health.changedArtifacts, 3)}` : ""
      ].filter(Boolean).join(" / "),
      className: health.hasContractDrift || health.missingRequiredReads ? "lifecycle-warning" : "lifecycle-empty"
    }));
}

function benchmarkStageHealth(stage) {
  const readiness = stageRuntimeReadiness(stage);
  const outputReadiness = stageOutputReadiness(stage);
  const contract = stageContract(stage);
  const lifecycles = pipelineStageArtifactDeltas(stage);
  const changedArtifacts = lifecycles.filter((item) => item.changed || numericDelta(item) !== 0);
  const emptyDeclaredArtifacts = normalizeStringList(outputReadiness?.emptyDeclaredOutputs);
  const undeclaredArtifacts = normalizeStringList(contract.undeclaredChangedArtifacts);
  const missingRequired = arrayCount(stage?.missingRequiredReads);
  const missingOptional = arrayCount(stage?.missingOptionalReads);
  const emptyRequiredArtifacts = normalizeStringList(readiness?.emptyRequiredReads);
  const emptyOptionalArtifacts = normalizeStringList(readiness?.emptyOptionalReads);
  return {
    hasDependencyGap: stage?.isDependencyReady === false || missingRequired > 0 || missingOptional > 0,
    hasContractDrift: contract.writesOnlyDeclaredArtifacts === false || undeclaredArtifacts.length > 0,
    missingRequiredReads: missingRequired,
    missingOptionalReads: missingOptional,
    emptyRequiredReads: emptyRequiredArtifacts.length,
    emptyOptionalReads: emptyOptionalArtifacts.length,
    undeclaredChangedArtifacts: undeclaredArtifacts.length,
    emptyDeclaredOutputs: emptyDeclaredArtifacts.length,
    emptyRequiredArtifacts,
    emptyOptionalArtifacts,
    undeclaredArtifacts,
    emptyDeclaredArtifacts,
    changedArtifacts
  };
}

function stageContract(stage) {
  if (stage?.contract) {
    return stage.contract;
  }

  const declaredWrites = normalizeStringList(stage?.writes);
  const changedArtifacts = normalizeStringList(
    Array.isArray(stage?.changedArtifacts)
      ? stage.changedArtifacts.map((item) => item?.artifact ?? item)
      : []);
  const declared = new Set(declaredWrites);
  const changed = new Set(changedArtifacts);
  const undeclaredChangedArtifacts = changedArtifacts.filter((artifact) => !declared.has(artifact));
  const declaredUnchangedArtifacts = declaredWrites.filter((artifact) => !changed.has(artifact));
  return {
    writesOnlyDeclaredArtifacts: undeclaredChangedArtifacts.length === 0,
    declaredWrites,
    changedArtifacts,
    undeclaredChangedArtifacts,
    declaredUnchangedArtifacts
  };
}

function pipelineExecutionWaveItems(scan) {
  return pipelineExecutionWaves(scan)
    .slice()
    .sort((first, second) => nonNegativeInteger(first.level) - nonNegativeInteger(second.level))
    .slice(0, 18)
    .map((wave) => {
      const hasConflicts = wave.hasWriteConflicts || arrayCount(wave.writeConflictArtifacts) > 0;
      const hasDependencies = wave.hasIntraWaveDependencies || arrayCount(wave.intraWaveDependencies) > 0;
      return {
        title: `Wave ${nonNegativeInteger(wave.level) || "-"} - ${arrayCount(wave.stages)} stage${arrayCount(wave.stages) === 1 ? "" : "s"}`,
        meta: [
          wave.isParallelCandidate ? "parallel candidate" : "serial",
          hasConflicts ? `write conflicts ${listSummary(wave.writeConflictArtifacts, 3)}` : "no write conflicts",
          hasDependencies ? `${arrayCount(wave.intraWaveDependencies)} intra-wave deps` : "no intra-wave deps",
          listSummary(wave.stages, 4),
          `writes ${listSummary(wave.writes, 4)}`
        ].filter(Boolean).join(" / ")
      };
    });
}

function pipelineArtifactLedgerRows(scan) {
  const stages = pipelineStages(scan);
  const diagnostics = normalizedDiagnostics(scan);
  const ledgerStages = stages.filter(stageHasArtifactLedger);
  const changes = pipelineArtifactChanges(scan);
  const lifecycles = pipelineArtifactLifecycles(scan);
  const positiveChanges = changes.filter((change) => numericDelta(change) > 0);
  const negativeChanges = changes.filter((change) => numericDelta(change) < 0);
  const totalAbsDelta = changes.reduce((total, change) => total + Math.abs(numericDelta(change)), 0);
  const artifactTotals = pipelineArtifactDeltaTotals(scan);
  const topArtifact = artifactTotals[0];
  const inventory = pipelineArtifactInventory(scan);
  const available = diagnostics?.availableArtifactCount ?? inventory.filter((item) => item.isPresent).length;
  const criticalMissing = diagnostics?.missingImportCriticalArtifactCount
    ?? inventory.filter((item) => item.isImportCritical && !item.isPresent).length;
  return [
    ["Final artifacts", inventory.length ? `${available} / ${inventory.length}` : "-"],
    ["Critical missing", criticalMissing],
    ["Stages with ledger", `${ledgerStages.length} / ${stages.length}`],
    ["Changed records", changes.length],
    ["Lifecycle records", lifecycles.length],
    ["Positive deltas", positiveChanges.length],
    ["Negative deltas", negativeChanges.length],
    ["Total abs delta", totalAbsDelta || "-"],
    ["Top artifact", topArtifact ? `${topArtifact.artifact} ${formatSigned(topArtifact.delta)}` : "-"],
    ["Output snapshots", stages.reduce((total, stage) => total + arrayCount(stage.outputArtifacts), 0)],
    ["Input snapshots", stages.reduce((total, stage) => total + arrayCount(stage.inputArtifacts), 0)]
  ];
}

function pipelineArtifactLifecycleRows(scan) {
  const lifecycles = pipelineArtifactLifecycles(scan);
  const byKind = lifecycleCountMap(lifecycles);
  return [
    ["Lifecycle rows", lifecycles.length],
    ["Changed", lifecycles.filter((item) => item.changed || numericDelta(item) !== 0).length],
    ["Created", byKind.get("Created") ?? 0],
    ["Increased", byKind.get("Increased") ?? 0],
    ["Decreased", byKind.get("Decreased") ?? 0],
    ["Removed", byKind.get("Removed") ?? 0],
    ["Unchanged", byKind.get("Unchanged") ?? 0],
    ["Declared outputs", lifecycles.filter((item) => item.isDeclaredWrite).length],
    ["Empty declared", pipelineOutputReadinessEmptyItems(scan).length],
    ["Undeclared changes", lifecycles.filter((item) => !item.isDeclaredWrite && (item.changed || numericDelta(item) !== 0)).length]
  ];
}

function pipelineStageLifecycleItems(scan) {
  return pipelineStages(scan)
    .filter(stageHasArtifactLedger)
    .slice()
    .sort((first, second) => nonNegativeInteger(first.order) - nonNegativeInteger(second.order)
      || String(first.stage || "").localeCompare(String(second.stage || "")))
    .slice(0, 36)
    .map((stage, index) => {
      const lifecycles = pipelineStageArtifactDeltas(stage);
      const changed = lifecycles.filter((item) => item.changed || numericDelta(item) !== 0);
      const outputReadiness = stageOutputReadiness(stage);
      const emptyDeclaredArtifacts = normalizeStringList(outputReadiness?.emptyDeclaredOutputs);
      const created = lifecycles.filter((item) => artifactChangeKind(item) === "Created");
      const contract = stage.contract ?? {};
      const contractIssues = arrayCount(contract.undeclaredChangedArtifacts);
      const readiness = stageRuntimeReadiness(stage);
      const emptyRequired = arrayCount(readiness?.emptyRequiredReads);
      const emptyOptional = arrayCount(readiness?.emptyOptionalReads);
      const emptyOutputs = arrayCount(outputReadiness?.emptyDeclaredOutputs);
      return {
        title: `${String(nonNegativeInteger(stage.order) || index + 1).padStart(2, "0")} ${stage.displayName || stage.stage || "stage"}`,
        meta: [
          stage.kind || "",
          `wave ${nonNegativeInteger(stage.executionWave) || nonNegativeInteger(stage.dependencyLevel) || "-"}`,
          emptyRequired ? `empty required ${listSummary(readiness.emptyRequiredReads, 3)}` : "reads ready",
          emptyOptional ? `empty optional ${listSummary(readiness.emptyOptionalReads, 2)}` : "",
          changed.length ? `changed ${pipelineArtifactLifecycleSummary(changed, 4)}` : "no changes",
          created.length ? `created ${listSummary(created.map((item) => item.artifact), 3)}` : "",
          emptyDeclaredArtifacts.length ? `empty ${listSummary(emptyDeclaredArtifacts, 4)}` : "",
          contractIssues ? `contract drift ${listSummary(contract.undeclaredChangedArtifacts, 3)}` : "contract ok"
        ].filter(Boolean).join(" / "),
        className: contractIssues || emptyRequired ? "lifecycle-warning" : changed.length ? "lifecycle-created" : "lifecycle-empty"
      };
    });
}

function pipelineArtifactLifecycleItems(scan) {
  return pipelineArtifactLifecycles(scan)
    .slice()
    .sort(compareArtifactLifecycleRows)
    .slice(0, 32)
    .map((item) => pipelineArtifactLifecycleListItem(item));
}

function pipelineEmptyDeclaredOutputItems(scan) {
  return pipelineOutputReadinessEmptyItems(scan)
    .slice()
    .sort((first, second) => String(first.stage || "").localeCompare(String(second.stage || ""))
      || String(first.artifact || "").localeCompare(String(second.artifact || "")))
    .slice(0, 28)
    .map((item) => ({
      title: `${item.stage || "stage"} -> ${item.artifact || "artifact"}`,
      meta: [
        item.changeKind || artifactChangeKind(item),
        item.beforeCount !== undefined || item.afterCount !== undefined
          ? `${formatLedgerCount(item.beforeCount)} -> ${formatLedgerCount(item.afterCount)}`
          : "",
        "declared output count is zero",
        listSummary(item.evidence, 2)
      ].filter(Boolean).join(" / "),
      className: "lifecycle-empty"
    }));
}

function pipelineRuntimeReadinessItems(scan) {
  return pipelineStages(scan)
    .map((stage, index) => ({ stage, index, readiness: stageRuntimeReadiness(stage) }))
    .filter((item) => item.readiness?.hasEmptyRequiredReads || item.readiness?.hasEmptyOptionalReads)
    .slice()
    .sort((first, second) =>
      Number(Boolean(second.readiness?.hasEmptyRequiredReads)) - Number(Boolean(first.readiness?.hasEmptyRequiredReads))
      || arrayCount(second.readiness?.emptyRequiredReads) - arrayCount(first.readiness?.emptyRequiredReads)
      || arrayCount(second.readiness?.emptyOptionalReads) - arrayCount(first.readiness?.emptyOptionalReads)
      || nonNegativeInteger(first.stage.order) - nonNegativeInteger(second.stage.order)
      || String(first.stage.stage || "").localeCompare(String(second.stage.stage || "")))
    .slice(0, 30)
    .map(({ stage, index, readiness }) => ({
      title: `${String(nonNegativeInteger(stage.order) || index + 1).padStart(2, "0")} ${stage.displayName || stage.stage || "stage"}`,
      meta: [
        readiness.hasEmptyRequiredReads ? `empty required ${listSummary(readiness.emptyRequiredReads, 5)}` : "required reads ready",
        readiness.hasEmptyOptionalReads ? `empty optional ${listSummary(readiness.emptyOptionalReads, 4)}` : "",
        arrayCount(readiness.nonEmptyRequiredReads) ? `has ${listSummary(readiness.nonEmptyRequiredReads, 4)}` : "no required read data",
        runtimeReadinessCountSummary(readiness)
      ].filter(Boolean).join(" / "),
      className: readiness.hasEmptyRequiredReads ? "lifecycle-warning" : "lifecycle-empty"
    }));
}

function pipelineArtifactInventoryItems(scan) {
  return pipelineArtifactInventory(scan)
    .slice()
    .sort((first, second) => Number(second.isImportCritical) - Number(first.isImportCritical)
      || Number(first.isPresent) - Number(second.isPresent)
      || nonNegativeInteger(second.count) - nonNegativeInteger(first.count)
      || String(first.artifact || "").localeCompare(String(second.artifact || "")))
    .slice(0, 42)
    .map((item) => ({
      title: `${item.artifact || "artifact"} ${formatLedgerCount(item.count)}`,
      meta: [
        item.isPresent ? "present" : "empty",
        item.isImportCritical ? "import critical" : "",
        item.isSourceArtifact ? "source" : "",
        item.importance || "",
        item.readinessImpact || "",
        arrayCount(item.producerStages) ? `from ${listSummary(item.producerStages, 3)}` : "",
        arrayCount(item.consumerStages) ? `used by ${listSummary(item.consumerStages, 3)}` : ""
      ].filter(Boolean).join(" / ")
    }));
}

function pipelineStageListItems(scan) {
  return pipelineStages(scan)
    .slice()
    .sort((first, second) => nonNegativeInteger(first.order) - nonNegativeInteger(second.order)
      || nonNegativeInteger(first.dependencyLevel) - nonNegativeInteger(second.dependencyLevel)
      || String(first.stage || "").localeCompare(String(second.stage || "")))
    .slice(0, 32)
    .map((stage, index) => {
      const hardLevel = nonNegativeInteger(stage.dependencyLevel);
      const preferredLevel = nonNegativeInteger(stage.preferredDependencyLevel);
      const missingRequired = arrayCount(stage.missingRequiredReads);
      const missingOptional = arrayCount(stage.missingOptionalReads);
      const changedArtifacts = pipelineStageChangedArtifacts(stage);
      const contract = stage.contract ?? {};
      const contractIssues = arrayCount(contract.undeclaredChangedArtifacts);
      const readiness = stageRuntimeReadiness(stage);
      const emptyRequired = arrayCount(readiness?.emptyRequiredReads);
      const emptyOptional = arrayCount(readiness?.emptyOptionalReads);
      return {
        title: `${String(nonNegativeInteger(stage.order) || index + 1).padStart(2, "0")} ${stage.displayName || stage.stage || "stage"}`,
        meta: [
          stage.kind || "",
          `wave ${nonNegativeInteger(stage.executionWave) || hardLevel || "-"}`,
          stage.isParallelCandidate ? "parallel candidate" : "",
          `L${hardLevel || "-"}`,
          `pref L${preferredLevel || "-"}`,
          stage.isDependencyReady === false ? `${missingRequired} missing required` : "ready",
          missingOptional ? `${missingOptional} missing optional` : "",
          emptyRequired ? `${emptyRequired} empty required data` : "",
          emptyOptional ? `${emptyOptional} empty optional data` : "",
          emptyOutputs ? `${emptyOutputs} empty outputs` : "",
          `reads ${arrayCount(stage.reads)}`,
          `writes ${arrayCount(stage.writes)}`,
          contractIssues ? `contract drift ${listSummary(contract.undeclaredChangedArtifacts, 3)}` : "contract ok",
          changedArtifacts.length ? `changes ${pipelineArtifactChangeSummary(changedArtifacts, 3)}` : "",
          listSummary(stage.capabilities, 3)
        ].filter(Boolean).join(" / ")
      };
    });
}

function pipelineNoisyArtifactChangeItems(scan) {
  return pipelineArtifactChanges(scan)
    .sort((first, second) => Math.abs(numericDelta(second)) - Math.abs(numericDelta(first))
      || String(first.stage || "").localeCompare(String(second.stage || ""))
      || String(first.artifact || "").localeCompare(String(second.artifact || "")))
    .slice(0, 18)
    .map((change) => ({
      title: `${change.stage || "stage"} -> ${change.artifact || "artifact"}`,
      meta: [
        `${formatLedgerCount(change.beforeCount)} -> ${formatLedgerCount(change.afterCount)}`,
        `delta ${formatSigned(numericDelta(change))}`,
        Math.abs(numericDelta(change)) >= 50 ? "review large jump" : ""
      ].filter(Boolean).join(" / ")
    }));
}

function pipelineStageContractIssueItems(scan) {
  return pipelineStages(scan)
    .filter((stage) => arrayCount(stage?.contract?.undeclaredChangedArtifacts) > 0
      || stage?.contract?.writesOnlyDeclaredArtifacts === false)
    .slice()
    .sort((first, second) => nonNegativeInteger(first.order) - nonNegativeInteger(second.order)
      || String(first.stage || "").localeCompare(String(second.stage || "")))
    .slice(0, 18)
    .map((stage, index) => ({
      title: `${String(nonNegativeInteger(stage.order) || index + 1).padStart(2, "0")} ${stage.displayName || stage.stage || "stage"}`,
      meta: [
        `undeclared ${listSummary(stage.contract?.undeclaredChangedArtifacts, 5)}`,
        `changed ${listSummary(stage.contract?.changedArtifacts, 5)}`,
        `declared ${listSummary(stage.contract?.declaredWrites, 5)}`
      ].filter(Boolean).join(" / ")
    }));
}

function pipelineStageArtifactSnapshotItems(scan) {
  return pipelineStages(scan)
    .filter(stageHasArtifactLedger)
    .slice()
    .sort((first, second) => nonNegativeInteger(first.order) - nonNegativeInteger(second.order)
      || String(first.stage || "").localeCompare(String(second.stage || "")))
    .slice(0, 32)
    .map((stage, index) => ({
      title: `${String(nonNegativeInteger(stage.order) || index + 1).padStart(2, "0")} ${stage.displayName || stage.stage || "stage"}`,
      meta: [
        `in ${pipelineArtifactSnapshotSummary(stage.inputArtifacts, 3)}`,
        `out ${pipelineArtifactSnapshotSummary(stage.outputArtifacts, 3)}`,
        `delta ${pipelineArtifactLifecycleSummary(pipelineStageArtifactDeltas(stage), 4)}`
      ].join(" / ")
    }));
}

function pipelinePlanIssueItems(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  return (diagnostics?.planIssues ?? [])
    .slice(0, 16)
    .map((issue) => ({
      title: issue.code || issue.stage || "pipeline.plan.issue",
      meta: [
        issue.severity || "Info",
        issue.stage || "",
        listSummary(issue.artifacts, 4),
        issue.message || ""
      ].filter(Boolean).join(" / ")
    }));
}

function pipelineStages(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  return Array.isArray(diagnostics?.stages) ? diagnostics.stages : [];
}

function pipelineExecutionWaves(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  return Array.isArray(diagnostics?.executionWaves) ? diagnostics.executionWaves : [];
}

function pipelineContractCounts(scan) {
  const stages = pipelineStages(scan);
  const contractStages = stages.filter((stage) => stage?.contract);
  const violations = contractStages.filter((stage) => stage.contract?.writesOnlyDeclaredArtifacts === false
    || arrayCount(stage.contract?.undeclaredChangedArtifacts) > 0).length;
  return {
    stages: contractStages.length,
    violations
  };
}

function stageHasArtifactLedger(stage) {
  return arrayCount(stage?.inputArtifacts) > 0
    || arrayCount(stage?.outputArtifacts) > 0
    || arrayCount(stage?.changedArtifacts) > 0
    || arrayCount(stage?.artifactDeltas) > 0
    || Boolean(stage?.outputReadiness);
}

function pipelineArtifactChanges(scan) {
  return pipelineStages(scan)
    .flatMap((stage) => pipelineStageChangedArtifacts(stage)
      .map((change) => ({
        ...change,
        stage: stage.stage || stage.displayName || "stage"
      })));
}

function pipelineArtifactInventory(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  return Array.isArray(diagnostics?.artifactInventory) ? diagnostics.artifactInventory : [];
}

function pipelineStageChangedArtifacts(stage) {
  return Array.isArray(stage?.changedArtifacts) ? stage.changedArtifacts : [];
}

function stageRuntimeReadiness(stage) {
  if (!stage) {
    return null;
  }

  if (stage.runtimeReadiness) {
    return stage.runtimeReadiness;
  }

  const counts = new Map();
  for (const item of Array.isArray(stage.inputArtifacts) ? stage.inputArtifacts : []) {
    const artifact = String(item?.artifact || "");
    if (!artifact) {
      continue;
    }

    counts.set(artifact, Math.max(counts.get(artifact) ?? 0, nonNegativeInteger(item?.count)));
  }

  const requiredReads = normalizeStringList(stage.reads);
  const optionalReads = normalizeStringList(stage.optionalReads);
  const nonEmptyRequiredReads = requiredReads.filter((artifact) => (counts.get(artifact) ?? 0) > 0);
  const emptyRequiredReads = requiredReads.filter((artifact) => (counts.get(artifact) ?? 0) === 0);
  const nonEmptyOptionalReads = optionalReads.filter((artifact) => (counts.get(artifact) ?? 0) > 0);
  const emptyOptionalReads = optionalReads.filter((artifact) => (counts.get(artifact) ?? 0) === 0);
  const evidence = [
    `required runtime reads with data ${nonEmptyRequiredReads.length}/${requiredReads.length}`,
    `optional runtime reads with data ${nonEmptyOptionalReads.length}/${optionalReads.length}`
  ];

  if (emptyRequiredReads.length) {
    evidence.push(`empty required runtime reads: ${emptyRequiredReads.join(",")}`);
  }

  if (emptyOptionalReads.length) {
    evidence.push(`empty optional runtime reads: ${emptyOptionalReads.join(",")}`);
  }

  return {
    requiredReadsHaveData: emptyRequiredReads.length === 0,
    hasEmptyRequiredReads: emptyRequiredReads.length > 0,
    nonEmptyRequiredReads,
    emptyRequiredReads,
    optionalReadsHaveData: optionalReads.length === 0 || emptyOptionalReads.length === 0,
    hasEmptyOptionalReads: emptyOptionalReads.length > 0,
    nonEmptyOptionalReads,
    emptyOptionalReads,
    evidence
  };
}

function stageOutputReadiness(stage) {
  if (!stage) {
    return null;
  }

  if (stage.outputReadiness) {
    return stage.outputReadiness;
  }

  const declaredWrites = normalizeStringList(stage.writes);
  const outputCounts = new Map();
  for (const item of Array.isArray(stage.outputArtifacts) ? stage.outputArtifacts : []) {
    const artifact = String(item?.artifact || "");
    if (!artifact) {
      continue;
    }

    outputCounts.set(artifact, Math.max(outputCounts.get(artifact) ?? 0, nonNegativeInteger(item?.count)));
  }

  const lifecycles = pipelineStageArtifactDeltas(stage);
  const changedArtifacts = new Set(
    lifecycles
      .filter((item) => item.changed || numericDelta(item) !== 0)
      .map((item) => String(item.artifact || ""))
      .filter(Boolean));
  const contract = stageContract(stage);
  const nonEmptyDeclaredOutputs = declaredWrites.filter((artifact) => (outputCounts.get(artifact) ?? 0) > 0);
  const emptyDeclaredOutputs = declaredWrites.filter((artifact) => (outputCounts.get(artifact) ?? 0) === 0);
  const changedDeclaredOutputs = declaredWrites.filter((artifact) => changedArtifacts.has(artifact));
  const unchangedDeclaredOutputs = declaredWrites.filter((artifact) => !changedArtifacts.has(artifact));
  const undeclaredChangedArtifacts = normalizeStringList(contract.undeclaredChangedArtifacts);
  const evidence = [
    `declared outputs with data ${nonEmptyDeclaredOutputs.length}/${declaredWrites.length}`,
    `changed declared outputs ${changedDeclaredOutputs.length}/${declaredWrites.length}`
  ];

  if (emptyDeclaredOutputs.length) {
    evidence.push(`empty declared outputs: ${emptyDeclaredOutputs.join(",")}`);
  }

  if (undeclaredChangedArtifacts.length) {
    evidence.push(`undeclared changed artifacts: ${undeclaredChangedArtifacts.join(",")}`);
  }

  return {
    declaredOutputsHaveData: emptyDeclaredOutputs.length === 0,
    hasEmptyDeclaredOutputs: emptyDeclaredOutputs.length > 0,
    nonEmptyDeclaredOutputs,
    emptyDeclaredOutputs,
    changedDeclaredOutputs,
    unchangedDeclaredOutputs,
    hasUndeclaredChanges: undeclaredChangedArtifacts.length > 0,
    undeclaredChangedArtifacts,
    evidence
  };
}

function pipelineOutputReadinessEmptyItems(scan) {
  return pipelineStages(scan).flatMap((stage) => {
    const readiness = stageOutputReadiness(stage);
    const emptyArtifacts = normalizeStringList(readiness?.emptyDeclaredOutputs);
    if (!emptyArtifacts.length) {
      return [];
    }

    const lifecycles = pipelineStageArtifactDeltas(stage);
    return emptyArtifacts.map((artifact) => {
      const lifecycle = lifecycles.find((item) => item?.artifact === artifact);
      return {
        ...(lifecycle ?? {}),
        artifact,
        stage: stage.stage || stage.displayName || "stage",
        evidence: normalizeStringList(readiness?.evidence).length
          ? normalizeStringList(readiness?.evidence)
          : normalizeStringList(lifecycle?.evidence),
        isDeclaredWrite: true,
        isEmptyDeclaredOutput: true
      };
    });
  });
}

function runtimeReadinessCountSummary(readiness) {
  const requiredWithData = arrayCount(readiness?.nonEmptyRequiredReads);
  const requiredTotal = requiredWithData + arrayCount(readiness?.emptyRequiredReads);
  const optionalWithData = arrayCount(readiness?.nonEmptyOptionalReads);
  const optionalTotal = optionalWithData + arrayCount(readiness?.emptyOptionalReads);
  return `required data ${requiredWithData}/${requiredTotal}, optional data ${optionalWithData}/${optionalTotal}`;
}

function pipelineArtifactLifecycles(scan) {
  return pipelineStages(scan)
    .flatMap((stage) => pipelineStageArtifactDeltas(stage)
      .map((delta) => ({
        ...delta,
        stage: stage.stage || stage.displayName || "stage"
      })));
}

function pipelineStageArtifactDeltas(stage) {
  if (Array.isArray(stage?.artifactDeltas) && stage.artifactDeltas.length) {
    return stage.artifactDeltas;
  }

  return pipelineStageChangedArtifacts(stage).map((change) => ({
    ...change,
    changeKind: artifactChangeKind(change),
    changed: numericDelta(change) !== 0,
    wasPresent: Number(change?.beforeCount) > 0,
    isPresent: Number(change?.afterCount) > 0,
    isDeclaredWrite: Array.isArray(stage?.writes) ? stage.writes.includes(change.artifact) : true,
    isEmptyDeclaredOutput: false,
    evidence: []
  }));
}

function pipelineArtifactDeltaTotals(scan) {
  const totals = new Map();
  const rows = pipelineArtifactLifecycles(scan).filter((item) => item.changed || numericDelta(item) !== 0);
  for (const change of rows.length ? rows : pipelineArtifactChanges(scan)) {
    const artifact = String(change.artifact || "Unknown");
    totals.set(artifact, (totals.get(artifact) ?? 0) + numericDelta(change));
  }

  return [...totals.entries()]
    .map(([artifact, delta]) => ({ artifact, delta }))
    .sort((first, second) => Math.abs(second.delta) - Math.abs(first.delta)
      || String(first.artifact).localeCompare(String(second.artifact)));
}

function pipelineArtifactSnapshotSummary(items, limit = 4) {
  if (!Array.isArray(items) || !items.length) {
    return "-";
  }

  return items
    .slice(0, limit)
    .map((item) => `${item.artifact || "artifact"} ${formatLedgerCount(item.count)}`)
    .join(", ")
    + (items.length > limit ? ` +${items.length - limit}` : "");
}

function pipelineArtifactChangeSummary(items, limit = 4) {
  if (!Array.isArray(items) || !items.length) {
    return "-";
  }

  return items
    .slice(0, limit)
    .map((item) => `${item.artifact || "artifact"} ${formatSigned(numericDelta(item))}`)
    .join(", ")
    + (items.length > limit ? ` +${items.length - limit}` : "");
}

function pipelineArtifactLifecycleSummary(items, limit = 4) {
  if (!Array.isArray(items) || !items.length) {
    return "-";
  }

  return items
    .slice(0, limit)
    .map((item) => `${item.artifact || "artifact"} ${artifactChangeKind(item)} ${formatSigned(numericDelta(item))}`)
    .join(", ")
    + (items.length > limit ? ` +${items.length - limit}` : "");
}

function pipelineArtifactLifecycleListItem(item) {
  const kind = artifactChangeKind(item);
  return {
    title: `${item.stage || "stage"} -> ${item.artifact || "artifact"}`,
    meta: [
      kind,
      `${formatLedgerCount(item.beforeCount)} -> ${formatLedgerCount(item.afterCount)}`,
      `delta ${formatSigned(numericDelta(item))}`,
      item.isDeclaredWrite ? "declared" : "undeclared",
      item.isEmptyDeclaredOutput ? "empty declared output" : "",
      listSummary(item.evidence, 2)
    ].filter(Boolean).join(" / "),
    className: lifecycleClassName(item)
  };
}

function compareArtifactLifecycleRows(first, second) {
  const firstWeight = lifecyclePriority(first);
  const secondWeight = lifecyclePriority(second);
  return secondWeight - firstWeight
    || Math.abs(numericDelta(second)) - Math.abs(numericDelta(first))
    || String(first.stage || "").localeCompare(String(second.stage || ""))
    || String(first.artifact || "").localeCompare(String(second.artifact || ""));
}

function lifecyclePriority(item) {
  if (!item?.isDeclaredWrite && (item?.changed || numericDelta(item) !== 0)) {
    return 6;
  }

  if (item?.isEmptyDeclaredOutput) {
    return 5;
  }

  switch (artifactChangeKind(item)) {
    case "Removed":
      return 4;
    case "Created":
      return 3;
    case "Increased":
    case "Decreased":
      return 2;
    default:
      return 1;
  }
}

function lifecycleClassName(item) {
  if (!item?.isDeclaredWrite && (item?.changed || numericDelta(item) !== 0)) {
    return "lifecycle-warning";
  }

  if (item?.isEmptyDeclaredOutput) {
    return "lifecycle-empty";
  }

  switch (artifactChangeKind(item)) {
    case "Created":
      return "lifecycle-created";
    case "Increased":
      return "lifecycle-increased";
    case "Decreased":
    case "Removed":
      return "lifecycle-warning";
    default:
      return "lifecycle-unchanged";
  }
}

function artifactChangeKind(item) {
  if (item?.changeKind) {
    return String(item.changeKind);
  }

  const before = Number(item?.beforeCount);
  const after = Number(item?.afterCount);
  if (!Number.isFinite(before) || !Number.isFinite(after) || before === after) {
    return "Unchanged";
  }

  if (before === 0 && after > 0) {
    return "Created";
  }

  if (before > 0 && after === 0) {
    return "Removed";
  }

  return after > before ? "Increased" : "Decreased";
}

function lifecycleCountRows(items) {
  const counts = lifecycleCountMap(items);
  return [
    ["Created", counts.get("Created") ?? 0],
    ["Increased", counts.get("Increased") ?? 0],
    ["Decreased", counts.get("Decreased") ?? 0],
    ["Removed", counts.get("Removed") ?? 0],
    ["Unchanged", counts.get("Unchanged") ?? 0],
    ["Empty declared", items.filter((item) => item.isEmptyDeclaredOutput).length]
  ];
}

function lifecycleCountMap(items) {
  const counts = new Map();
  (items ?? []).forEach((item) => {
    const kind = artifactChangeKind(item);
    counts.set(kind, (counts.get(kind) ?? 0) + 1);
  });
  return counts;
}

function numericDelta(change) {
  const delta = Number(change?.delta);
  if (Number.isFinite(delta)) {
    return delta;
  }

  const before = Number(change?.beforeCount);
  const after = Number(change?.afterCount);
  return Number.isFinite(before) && Number.isFinite(after) ? after - before : 0;
}

function formatLedgerCount(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number.toLocaleString() : "-";
}

function normalizedDiagnostics(scan) {
  const diagnostics = scan?.diagnostics;
  if (!diagnostics || Array.isArray(diagnostics)) {
    return null;
  }

  return diagnostics;
}

function pipelineIssueCounts(scan) {
  const diagnostics = normalizedDiagnostics(scan);
  const issues = Array.isArray(diagnostics?.planIssues) ? diagnostics.planIssues : [];
  return {
    total: diagnostics?.planIssueCount ?? issues.length,
    warnings: diagnostics?.planWarningCount ?? issues.filter((issue) => String(issue.severity || "").toLowerCase() === "warning").length,
    errors: diagnostics?.planErrorCount ?? issues.filter((issue) => String(issue.severity || "").toLowerCase() === "error").length
  };
}

function listSummary(values, limit = 4) {
  if (!Array.isArray(values) || !values.length) {
    return "-";
  }

  const visible = values.slice(0, limit).map((value) => String(value || "").trim()).filter(Boolean);
  if (!visible.length) {
    return "-";
  }

  const suffix = values.length > visible.length ? ` +${values.length - visible.length}` : "";
  return `${visible.join(", ")}${suffix}`;
}

function normalizeStringList(values) {
  if (!Array.isArray(values)) {
    return [];
  }

  return [...new Set(values
    .map((value) => String(value || "").trim())
    .filter(Boolean))]
    .sort((first, second) => first.localeCompare(second));
}

function arrayCount(values) {
  return Array.isArray(values) ? values.length : 0;
}

function sourceReadinessFallbackStatus(document) {
  if (!document || !Object.keys(document).length) {
    return "-";
  }

  if (document.isDwgDerived) {
    return "DwgSourceUnverified";
  }

  if (document.isRasterDerived) {
    return "RasterSourceUnverified";
  }

  if (document.sourceFormat || document.loader || document.ingestionPath) {
    return "SourceRecorded";
  }

  return "-";
}

function formatNullableBoolean(value) {
  if (value == null || value === "") {
    return "-";
  }

  return value ? "Yes" : "No";
}

function workspaceCoordinateRows(scan) {
  if (!scan) {
    if (state.visualSnapshot) {
      return visualSnapshotCoordinateRows(state.visualSnapshot);
    }

    return [
      ["Space", "-"],
      ["Origin", "-"],
      ["Axes", "-"],
      ["Page", "-"],
      ["Bounds", "-"]
    ];
  }

  const coordinateSystem = scan.coordinateSystem ?? normalizeCoordinateSystem(null, scan.pages, scan.calibration);
  const page = currentPageDefinition();
  const frame = page ? coordinateFrameForPage(coordinateSystem, page.number) : null;
  return [
    ["Space", coordinateSystem.coordinateSpace || "-"],
    ["Origin", coordinateSystem.origin || "-"],
    ["Axes", `${coordinateSystem.xAxisDirection || "Right"} / ${coordinateSystem.yAxisDirection || "Down"}`],
    ["Order", coordinateSystem.coordinateOrder || "x,y"],
    ["Page", page ? `${page.number}` : "-"],
    ["Bounds", frame?.bounds ? formatRectCoordinates(frame.bounds) : "-"],
    ["Normalize", frame?.pageToNormalizedTransform ? formatAffineTransform(frame.pageToNormalizedTransform) : "-"]
  ];
}

function visualSnapshotCurrentPage(snapshot = state.visualSnapshot) {
  if (!snapshot?.pages?.length) {
    return null;
  }

  return snapshot.pages.find((page) => Number(page.number) === Number(state.currentPage))
    ?? snapshot.pages.find((page) => Number(page.pageNumber) === Number(state.currentPage))
    ?? snapshot.pages[0];
}

function visualSnapshotCountRows(snapshot = state.visualSnapshot) {
  const page = visualSnapshotCurrentPage(snapshot);
  return [
    ["Pages", snapshot?.pages?.length ?? 0],
    ["Current page", page ? `${page.pageNumber}` : "-"],
    ["Snapshot layers", page?.layers?.length ?? 0],
    ["Drawable items", page?.drawableItemCount ?? 0],
    ["Primitive count", page?.primitiveCount ?? 0],
    ["Review queue", snapshot?.reviewQueueCount ?? 0],
    ["Issues", visualSnapshotIssues(snapshot).length],
    ["Quality", snapshot?.qualityGrade ?? "-"]
  ];
}

function visualSnapshotAnalysisRows(snapshot = state.visualSnapshot, page = visualSnapshotCurrentPage(snapshot), layerSummary = "-") {
  const issues = visualSnapshotIssuesForPage(page, snapshot);
  return [
    ["Current page", page ? `${page.pageNumber}` : "-"],
    ["Page size", page ? `${formatCoordinateNumber(page.width)} x ${formatCoordinateNumber(page.height)}` : "-"],
    ["Visible layers", layerSummary],
    ["Visible items", visualSnapshotVisibleItemCount(page)],
    ["All detections", page?.drawableItemCount ?? 0],
    ["Source layers", page?.layers?.length ?? 0],
    ["Review queue", page?.reviewQueueCount ?? snapshot?.reviewQueueCount ?? 0],
    ["Diagnostics", issues.length],
    ["Quality", snapshot?.qualityGrade ?? "-"]
  ];
}

function visualSnapshotCoordinateRows(snapshot = state.visualSnapshot) {
  const page = visualSnapshotCurrentPage(snapshot);
  const pageBounds = page?.pageBounds;
  return [
    ["Space", snapshot?.coordinateSpace || "-"],
    ["Origin", snapshot?.origin || "-"],
    ["Axes", `${snapshot?.xAxisDirection || "Right"} / ${snapshot?.yAxisDirection || "Down"}`],
    ["Order", "x,y"],
    ["Unit", snapshot?.unit || "-"],
    ["Page", page ? `${page.pageNumber}` : "-"],
    ["Bounds", pageBounds ? formatRectCoordinates(pageBounds) : "-"],
    ["Size", page ? `${formatCoordinateNumber(page.width)} x ${formatCoordinateNumber(page.height)}` : "-"],
    ["Normalize", page ? `x/pageWidth, y/pageHeight` : "-"],
    ["Schema", snapshot?.schemaVersion || "-"]
  ];
}

function visualSnapshotIssues(snapshot = state.visualSnapshot) {
  if (!snapshot) {
    return [];
  }

  return uniqueVisualSnapshotIssues([
    ...(Array.isArray(snapshot.issues) ? snapshot.issues : []),
    ...(snapshot.pages ?? []).flatMap((page) => Array.isArray(page.issues) ? page.issues : [])
  ]);
}

function uniqueVisualSnapshotIssues(issues) {
  const seen = new Set();
  return (issues ?? []).filter((issue) => {
    const key = [
      issue?.code || "",
      issue?.severity || "",
      issue?.pageNumber ?? "",
      issue?.message || ""
    ].join("|");
    if (seen.has(key)) {
      return false;
    }

    seen.add(key);
    return true;
  });
}

function visualSnapshotIssuesForPage(page = visualSnapshotCurrentPage(), snapshot = state.visualSnapshot) {
  if (!snapshot || !page) {
    return [];
  }

  return visualSnapshotIssues(snapshot)
    .filter((issue) => issue.pageNumber == null || Number(issue.pageNumber) === Number(page.pageNumber));
}

function visualSnapshotDensestLayers(page = visualSnapshotCurrentPage(), limit = 10) {
  return [...(page?.layers ?? [])]
    .filter((layer) => layer.count > 0)
    .sort((first, second) => (second.normalizedDensity ?? 0) - (first.normalizedDensity ?? 0)
      || (second.count ?? 0) - (first.count ?? 0)
      || String(first.name).localeCompare(String(second.name)))
    .slice(0, limit);
}

function visualSnapshotVisibleItemCount(page = visualSnapshotCurrentPage()) {
  return (page?.layers ?? [])
    .filter((layer) => {
      const key = visualSnapshotOverlayKey(layer.name);
      return !key || state.enabledLayers.has(key);
    })
    .reduce((total, layer) => total + (layer.count ?? 0), 0);
}

function visualSnapshotOverlayKey(name) {
  const normalized = String(name || "").toLowerCase();
  const direct = overlayLegendItems.find((item) => item.key.toLowerCase() === normalized);
  if (direct) {
    return direct.key;
  }

  switch (normalized) {
    case "titleblocks":
      return "regions";
    case "gridbayspacings":
      return "gridBays";
    case "wallnodes":
      return "nodes";
    case "roomadjacency":
      return "roomAdjacency";
    case "objectgroups":
      return "objects";
    case "routingbarriers":
    case "routingpassages":
    case "routingobstacles":
    case "routingroomusehints":
      return "routingLayer";
    default:
      return "";
  }
}

function snapshotLayerLabel(name) {
  return String(name || "Layer")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, (value) => value.toUpperCase());
}

function visualSnapshotLayerMeta(layer) {
  const parts = [
    `${layer.count ?? 0} item${layer.count === 1 ? "" : "s"}`,
    `density ${formatNumber(layer.normalizedDensity ?? 0, 1)}`,
    layer.averageConfidence == null ? "" : `avg ${formatNumber(layer.averageConfidence, 2)}`,
    layer.normalizedBounds ? `norm ${formatRectCoordinates(layer.normalizedBounds, 4)}` : ""
  ].filter(Boolean);
  const breakdown = Object.entries(layer.breakdown ?? {})
    .slice(0, 4)
    .map(([key, value]) => `${key} ${value}`)
    .join(", ");
  return [parts.join(" / "), breakdown].filter(Boolean).join(" / ");
}

function visualSnapshotQualityReport(snapshot = state.visualSnapshot) {
  if (!snapshot) {
    return null;
  }

  const issues = visualSnapshotIssues(snapshot);
  const layers = snapshot.pages.flatMap((page) => page.layers ?? []);
  const warningCount = issues.filter((issue) => String(issue.severity || "").toLowerCase() === "warning").length;
  const errorCount = issues.filter((issue) => String(issue.severity || "").toLowerCase() === "error").length;
  return {
    grade: snapshot.qualityGrade || "Unknown",
    overallConfidence: snapshot.qualityConfidence,
    requiresReview: Boolean(snapshot.requiresReview),
    detectorCount: layers.length,
    detectorWithFindingsCount: layers.filter((layer) => layer.count > 0).length,
    detectionCount: layers.reduce((total, layer) => total + (layer.count ?? 0), 0),
    diagnosticWarningCount: warningCount,
    diagnosticErrorCount: errorCount,
    issues: issues.map((issue) => ({
      code: issue.code || "visual.snapshot.issue",
      severity: issue.severity || "Info",
      message: issue.message || "",
      pageNumber: issue.pageNumber,
      properties: issue.properties ?? {}
    })),
    detectors: visualSnapshotDensestLayers(visualSnapshotCurrentPage(snapshot), 8).map((layer) => ({
      name: snapshotLayerLabel(layer.name),
      itemCount: layer.count ?? 0,
      averageConfidence: layer.averageConfidence ?? 0,
      reviewRequiredCount: (layer.normalizedDensity ?? 0) >= 1000 ? 1 : 0
    }))
  };
}

function visualSnapshotDiagnostics(snapshot = state.visualSnapshot) {
  if (!snapshot) {
    return null;
  }

  const messages = visualSnapshotIssues(snapshot).map((issue) => ({
    code: issue.code || "visual.snapshot.issue",
    severity: issue.severity || "Info",
    stage: "visual-snapshot",
    scope: "visual-review",
    pageNumber: issue.pageNumber,
    message: issue.message || "",
    confidence: issue.confidence,
    properties: issue.properties ?? {}
  }));
  return {
    infoCount: messages.filter((item) => String(item.severity).toLowerCase() === "info").length,
    warningCount: messages.filter((item) => String(item.severity).toLowerCase() === "warning").length,
    errorCount: messages.filter((item) => String(item.severity).toLowerCase() === "error").length,
    durationMilliseconds: null,
    stages: ["visual-snapshot"],
    messages
  };
}

function diagnosticItems(scan = state.scan) {
  if (!scan?.diagnostics) {
    return [];
  }

  return [
    ...(scan.diagnostics.messages ?? []),
    ...(scan.diagnostics.items ?? []),
    ...(scan.diagnostics.diagnostics ?? [])
  ];
}

function coordinateFrameForPage(coordinateSystem, pageNumber) {
  return (coordinateSystem?.pageFrames ?? []).find((frame) => Number(frame.pageNumber) === Number(pageNumber));
}

function pageDefinitionByNumber(pageNumber) {
  return (state.scan?.pages ?? []).find((page) => Number(page.number) === Number(pageNumber))
    ?? (state.visualSnapshot?.pages ?? []).find((page) => Number(page.number) === Number(pageNumber));
}

function visibleDetectionCount(scan) {
  return overlayLegendItemsForCurrentPayload(scan)
    .filter((item) => state.enabledLayers.has(item.key))
    .reduce((total, item) => total + layerCountForKey(scan, item.key), 0);
}

function totalDetectionCount(scan) {
  return [
    scan.regions,
    scan.dimensions,
    scan.gridAxes,
    scan.gridBaySpacings,
    scan.annotations,
    scan.surfacePatterns,
    scan.wallComponents,
    scan.walls,
    scan.nodes,
    scan.rooms,
    scan.roomAdjacencyEdges,
    scan.roomClusters,
    scan.openings,
    scan.objects,
    scan.objectAggregates,
    scan.wallGraphRepairCandidates,
    scan.reviewQueue,
    scan.routingLayer?.barriers,
    scan.routingLayer?.passages,
    scan.routingLayer?.obstacles,
    scan.routingLayer?.roomUseHints,
    scan.routingLayer?.suppressedObjects,
    scan.routingLayer?.ignoredObjects
  ].reduce((total, collection) => total + (collection?.length ?? 0), 0);
}

function wallTopologySpanCount(scan = state.scan, pageNumber = null, predicate = shouldDrawWallAsCleanTopologySpan) {
  return (scan?.walls ?? [])
    .filter(predicate)
    .filter((wall) => pageNumber == null || wall.pageNumber == null || wall.pageNumber === pageNumber)
    .reduce((total, wall) => total + wallCleanTopologySpans(wall).length, 0);
}

function wallBodyFootprintCount(scan = state.scan, pageNumber = null) {
  return (scan?.walls ?? [])
    .filter(shouldDrawWallAsCleanTopologySpan)
    .filter((wall) => pageNumber == null || wall.pageNumber == null || wall.pageNumber === pageNumber)
    .reduce((total, wall) => total + wallBodyFootprints(wall).length, 0);
}

function routingLayerItemCount(layer) {
  return (layer?.barriers?.length ?? 0)
    + (layer?.passages?.length ?? 0)
    + (layer?.obstacles?.length ?? 0)
    + (layer?.roomUseHints?.length ?? 0)
    + (layer?.suppressedObjects?.length ?? 0)
    + (layer?.ignoredObjects?.length ?? 0);
}

function overlayLegendItemsForCurrentPayload(scan = state.scan) {
  if (!scan && state.visualSnapshot) {
    const layerKeys = new Set((visualSnapshotCurrentPage()?.layers ?? [])
      .map((layer) => visualSnapshotOverlayKey(layer.name))
      .filter(Boolean));
    return overlayLegendItems.filter((item) => layerKeys.has(item.key));
  }

  if (!state.placement || !scan) {
    return overlayLegendItems;
  }

  return overlayLegendItems
    .filter((item) => placementOverlayLayerKeys.has(item.key))
    .filter((item) => layerTotalForKey(scan, item.key) > 0);
}

function availableOverlayLayerKeys(scan = state.scan) {
  return new Set(overlayLegendItemsForCurrentPayload(scan).map((item) => item.key));
}

function layerTotalForKey(scan, key) {
  switch (key) {
    case "regions":
      return scan.regions?.length ?? 0;
    case "dimensions":
      return scan.dimensions?.length ?? 0;
    case "gridAxes":
      return scan.gridAxes?.length ?? 0;
    case "gridBays":
      return scan.gridBaySpacings?.length ?? 0;
    case "annotations":
      return scan.annotations?.length ?? 0;
    case "surfacePatterns":
      return scan.surfacePatterns?.length ?? 0;
    case "placementIssues":
      return state.placement?.issues?.filter((issue) => normalizeRect(issue.bounds)).length ?? 0;
    case "wallComponents":
      return scan.wallComponents?.length ?? 0;
    case "walls":
      return wallTopologySpanCount(scan, null, shouldDrawWallAsPlacementWall);
    case "wallBodyFootprints":
      return wallBodyFootprintCount(scan);
    case "wallTopologySpans":
      return wallTopologySpanCount(scan);
    case "wallTopologyReviewSpans":
      return wallTopologySpanCount(scan, null, shouldDrawWallAsReviewTopologySpan);
    case "nodes":
      return scan.nodes?.length ?? 0;
    case "rooms":
      return scan.rooms?.length ?? 0;
    case "roomClusters":
      return scan.roomClusters?.length ?? 0;
    case "roomAdjacency":
      return scan.roomAdjacencyEdges?.length ?? 0;
    case "openings":
      return scan.openings?.length ?? 0;
    case "objects":
      return scan.objects?.length ?? 0;
    case "objectAggregates":
      return scan.objectAggregates?.length ?? 0;
    case "wallGraphRepairs":
      return scan.wallGraphRepairCandidates?.length ?? 0;
    case "suppressedDetails":
      return scanReviewQueue(scan).filter((item) => scanReviewQueueKindKey(item) === "suppressed-wall-pattern-review").length;
    case "wallGraphGaps":
      return scanReviewQueue(scan).filter((item) => scanReviewQueueKindKey(item) === "wall-graph-gap-review").length;
    case "reviewQueue":
      return scan.reviewQueue?.length ?? 0;
    case "routingLayer":
      return routingLayerItemCount(scan.routingLayer);
    case "benchmarkTargets":
      return activeBenchmarkTargets().length;
    case "compare":
      return state.compare ? Object.values(state.compare.layers ?? {}).reduce((total, layer) => total + (layer.added?.length ?? 0) + (layer.removed?.length ?? 0) + (layer.changed?.length ?? 0), 0) : 0;
    default:
      return 0;
  }
}

function layerCountForKey(scan, key) {
  const currentPageOnly = (items = []) => items.filter((item) => item.pageNumber == null || item.pageNumber === state.currentPage).length;
  switch (key) {
    case "regions":
      return currentPageOnly(scan.regions);
    case "dimensions":
      return currentPageOnly(scan.dimensions);
    case "gridAxes":
      return currentPageOnly(scan.gridAxes);
    case "gridBays":
      return currentPageOnly(scan.gridBaySpacings);
    case "annotations":
      return currentPageOnly(scan.annotations);
    case "surfacePatterns":
      return currentPageOnly(scan.surfacePatterns);
    case "placementIssues":
      return placementIssuesForPage(state.currentPage).filter((issue) => issue.bounds).length;
    case "wallComponents":
      return currentPageOnly(scan.wallComponents);
    case "walls":
      return wallTopologySpanCount(scan, state.currentPage, shouldDrawWallAsPlacementWall);
    case "wallBodyFootprints":
      return wallBodyFootprintCount(scan, state.currentPage);
    case "wallTopologySpans":
      return wallTopologySpanCount(scan, state.currentPage);
    case "wallTopologyReviewSpans":
      return wallTopologySpanCount(scan, state.currentPage, shouldDrawWallAsReviewTopologySpan);
    case "nodes":
      return currentPageOnly(scan.nodes);
    case "rooms":
      return currentPageOnly(scan.rooms);
    case "roomClusters":
      return currentPageOnly(scan.roomClusters);
    case "roomAdjacency":
      return currentPageOnly(scan.roomAdjacencyEdges);
    case "openings":
      return currentPageOnly(scan.openings);
    case "objects":
      return currentPageOnly(scan.objects);
    case "objectAggregates":
      return currentPageOnly(scan.objectAggregates);
    case "wallGraphRepairs":
      return currentPageOnly(scan.wallGraphRepairCandidates);
    case "suppressedDetails":
      return scanReviewQueueForPage(scan, state.currentPage)
        .filter((item) => scanReviewQueueKindKey(item) === "suppressed-wall-pattern-review").length;
    case "wallGraphGaps":
      return scanReviewQueueForPage(scan, state.currentPage)
        .filter((item) => scanReviewQueueKindKey(item) === "wall-graph-gap-review").length;
    case "reviewQueue":
      return scanReviewQueueForPage(scan, state.currentPage).length;
    case "routingLayer":
      return currentPageOnly(scan.routingLayer?.barriers)
        + currentPageOnly(scan.routingLayer?.passages)
        + currentPageOnly(scan.routingLayer?.obstacles)
        + currentPageOnly(scan.routingLayer?.roomUseHints)
        + currentPageOnly(scan.routingLayer?.suppressedObjects)
        + currentPageOnly(scan.routingLayer?.ignoredObjects);
    case "benchmarkTargets":
      return activeBenchmarkTargets().filter((target) => target.pageNumber == null || target.pageNumber === state.currentPage).length;
    case "compare":
      return state.compare ? Object.values(state.compare.layers ?? {}).reduce((total, layer) => total + (layer.added?.length ?? 0) + (layer.removed?.length ?? 0) + (layer.changed?.length ?? 0), 0) : 0;
    default:
      return 0;
  }
}

function diagnosticCount(scan) {
  const diagnostics = scan?.diagnostics;
  if (Array.isArray(diagnostics)) {
    return diagnostics.length;
  }

  return diagnostics?.messages?.length
    ?? diagnostics?.items?.length
    ?? diagnostics?.diagnostics?.length
    ?? 0;
}

function annotationReferenceCount(scan) {
  return (scan?.annotations ?? []).reduce((annotationTotal, annotation) =>
    annotationTotal + (annotation.items ?? []).reduce((itemTotal, item) =>
      itemTotal + (item.references?.length ?? 0), 0), 0);
}

function setCompare(comparison = null) {
  elements.compareDetails.replaceChildren();

  if (!comparison && state.benchmarkComparison) {
    renderBenchmarkComparisonDetails(state.benchmarkComparison);
    return;
  }

  if (!comparison && state.batchComparison) {
    renderBatchComparisonDetails(state.batchComparison);
    return;
  }

  if (!comparison) {
    elements.compareDetails.textContent = "No comparison";
    return;
  }

  const status = document.createElement("div");
  status.className = comparison.hasChanges ? "compare-status" : "compare-status clean";
  status.textContent = comparison.hasChanges
    ? `${comparison.changeCount} delta${comparison.changeCount === 1 ? "" : "s"} detected`
    : "No deltas detected";

  const list = document.createElement("dl");
  const rows = [
    ["Baseline", comparison.baselineLabel],
    ["Candidate", comparison.candidateLabel],
    ["Pages", `${comparison.baselinePageCount} -> ${comparison.candidatePageCount}`],
    ["Quality", `${comparison.quality.baselineGrade} -> ${comparison.quality.candidateGrade}`],
    ["Confidence", `${formatNumber(comparison.quality.baselineConfidence)} -> ${formatNumber(comparison.quality.candidateConfidence)} (${formatSigned(comparison.quality.confidenceDelta, 2)})`],
    ["Scan risks", `${comparison.quality.baselineScanRiskIssueCount} -> ${comparison.quality.candidateScanRiskIssueCount}`],
    ["Diagnostics", `${comparison.diagnostics.baselineErrorCount} -> ${comparison.diagnostics.candidateErrorCount} err`]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  const countList = document.createElement("div");
  countList.className = "compare-counts";
  comparison.counts.forEach((row) => {
    const item = document.createElement("div");
    item.className = "compare-row";

    const label = document.createElement("strong");
    label.textContent = row.label;

    const counts = document.createElement("span");
    counts.textContent = `${row.baselineCount} -> ${row.candidateCount}`;

    const delta = document.createElement("span");
    delta.className = `delta ${row.delta > 0 ? "up" : row.delta < 0 ? "down" : ""}`;
    delta.textContent = `${formatSigned(row.delta)} (${row.addedCount}+/${row.removedCount}-)`;

    item.append(label, counts, delta);
    countList.appendChild(item);
  });

  elements.compareDetails.append(status, list, countList);

  const notes = [
    comparison.quality.addedScanRiskCodes.length ? `New scan risks: ${comparison.quality.addedScanRiskCodes.join(", ")}` : "",
    comparison.quality.removedScanRiskCodes.length ? `Removed scan risks: ${comparison.quality.removedScanRiskCodes.join(", ")}` : "",
    comparison.quality.addedIssueCodes.length ? `New quality issues: ${comparison.quality.addedIssueCodes.join(", ")}` : "",
    comparison.quality.removedIssueCodes.length ? `Removed quality issues: ${comparison.quality.removedIssueCodes.join(", ")}` : "",
    comparison.diagnostics.addedCodes.length ? `New diagnostics: ${comparison.diagnostics.addedCodes.join(", ")}` : "",
    comparison.diagnostics.removedCodes.length ? `Removed diagnostics: ${comparison.diagnostics.removedCodes.join(", ")}` : "",
    comparison.quality.gradeChanged ? "Scan quality grade changed." : ""
  ].filter(Boolean);

  notes.forEach((text) => {
    const note = document.createElement("p");
    note.className = "compare-note";
    note.textContent = text;
    elements.compareDetails.appendChild(note);
  });
}

function renderBenchmarkComparisonDetails(comparison) {
  const status = document.createElement("div");
  status.className = comparison.passed ? "compare-status clean" : "compare-status batch-regression";
  status.textContent = comparison.passed
    ? `PASS - ${comparison.cases.length} benchmark case${comparison.cases.length === 1 ? "" : "s"} compared`
    : `REGRESSION - ${comparison.regressionCount} regression signal${comparison.regressionCount === 1 ? "" : "s"}`;

  const list = document.createElement("dl");
  [
    ["Baseline", comparison.baselineName || "-"],
    ["Candidate", comparison.candidateName || "-"],
    ["Cases", `${comparison.matchedCaseCount} matched, ${comparison.addedCaseCount} added, ${comparison.removedCaseCount} removed`],
    ["Signals", `${comparison.regressionCount} regression / ${comparison.improvementCount} improvement / ${comparison.infoCount} info`],
    ["Failed cases", `${comparison.cases.filter(benchmarkComparisonCaseFailed).length}`],
    ["Skipped cases", `${comparison.cases.filter(benchmarkComparisonCaseSkipped).length}`],
    ["Generated", comparison.generatedAt || "-"]
  ].forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.compareDetails.append(status, list);

  const signals = comparison.signals
    .slice()
    .sort(compareBenchmarkComparisonSignals)
    .slice(0, 18);
  elements.compareDetails.appendChild(renderBenchmarkComparisonSignalList(signals));

  const cases = comparison.cases
    .slice()
    .sort(compareBenchmarkComparisonCases)
    .slice(0, 80);
  elements.compareDetails.appendChild(renderBenchmarkComparisonCaseList(cases));

  if (comparison.cases.length > 80) {
    const overflow = document.createElement("p");
    overflow.className = "compare-note";
    overflow.textContent = `${comparison.cases.length - 80} more compared cases available in the JSON.`;
    elements.compareDetails.appendChild(overflow);
  }
}

function renderBenchmarkComparisonSignalList(signals) {
  const wrapper = document.createElement("div");
  wrapper.className = "batch-comparison-list";

  const heading = document.createElement("strong");
  heading.className = "batch-comparison-heading";
  heading.textContent = "Top benchmark signals";
  wrapper.appendChild(heading);

  if (!signals.length) {
    const empty = document.createElement("p");
    empty.className = "compare-note";
    empty.textContent = "No regression or improvement signals.";
    wrapper.appendChild(empty);
    return wrapper;
  }

  signals.forEach((signal) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `batch-comparison-row signal ${signal.severity.toLowerCase()}`;

    const title = document.createElement("strong");
    title.textContent = signal.code;

    const message = document.createElement("span");
    message.textContent = signal.message || signal.fixtureId;

    const meta = document.createElement("small");
    meta.textContent = [signal.severity, signal.fixtureId, benchmarkComparisonSignalPair(signal)].filter(Boolean).join(" / ");

    button.append(title, message, meta);
    button.addEventListener("click", () => {
      state.selectedItem = describeBenchmarkComparisonSignal(signal);
      setSelection(state.selectedItem);
      refreshWorkspaceTabs();
    });
    wrapper.appendChild(button);
  });

  return wrapper;
}

function renderBenchmarkComparisonCaseList(cases) {
  const wrapper = document.createElement("div");
  wrapper.className = "batch-comparison-list";

  const heading = document.createElement("strong");
  heading.className = "batch-comparison-heading";
  heading.textContent = "Compared benchmark cases";
  wrapper.appendChild(heading);

  if (!cases.length) {
    const empty = document.createElement("p");
    empty.className = "compare-note";
    empty.textContent = "No compared cases.";
    wrapper.appendChild(empty);
    return wrapper;
  }

  cases.forEach((item) => {
    const severity = benchmarkComparisonCaseSeverity(item).toLowerCase();
    const button = document.createElement("button");
    button.type = "button";
    button.className = `batch-comparison-row item ${String(item.status || "").toLowerCase()} ${severity}`;

    const title = document.createElement("strong");
    title.textContent = benchmarkComparisonCaseName(item);

    const metrics = document.createElement("span");
    metrics.textContent = [
      item.status,
      benchmarkComparisonPassPair(item),
      benchmarkComparisonQualityPair(item),
      `failed assertions ${intPair(item.baselineFailedAssertionCount, item.candidateFailedAssertionCount)}`
    ].filter(Boolean).join(" / ");

    const meta = document.createElement("small");
    meta.textContent = [
      `${item.signals.length} signal${item.signals.length === 1 ? "" : "s"}`,
      benchmarkComparisonTopDelta(item),
      benchmarkComparisonDurationPair(item)
    ].filter(Boolean).join(" / ");

    button.append(title, metrics, meta);
    button.addEventListener("click", () => {
      state.selectedItem = describeBenchmarkComparisonCase(item);
      setSelection(state.selectedItem);
      refreshWorkspaceTabs();
    });
    wrapper.appendChild(button);
  });

  return wrapper;
}

function compareBenchmarkComparisonSignals(first, second) {
  return benchmarkComparisonSeverityRank(first.severity) - benchmarkComparisonSeverityRank(second.severity)
    || String(first.fixtureId).localeCompare(String(second.fixtureId))
    || String(first.code).localeCompare(String(second.code));
}

function compareBenchmarkComparisonCases(first, second) {
  return benchmarkComparisonSeverityRank(benchmarkComparisonCaseSeverity(first)) - benchmarkComparisonSeverityRank(benchmarkComparisonCaseSeverity(second))
    || benchmarkComparisonStatusRank(first.status) - benchmarkComparisonStatusRank(second.status)
    || String(benchmarkComparisonCaseName(first)).localeCompare(benchmarkComparisonCaseName(second));
}

function benchmarkComparisonSeverityRank(severity) {
  switch (String(severity || "").toLowerCase()) {
    case "regression":
      return 0;
    case "improvement":
      return 1;
    default:
      return 2;
  }
}

function benchmarkComparisonStatusRank(status) {
  switch (String(status || "").toLowerCase()) {
    case "removed":
      return 0;
    case "added":
      return 1;
    default:
      return 2;
  }
}

function benchmarkComparisonCaseSeverity(item) {
  if (item.signals?.some((signal) => signal.severity === "Regression")) {
    return "Regression";
  }

  if (item.signals?.some((signal) => signal.severity === "Improvement")) {
    return "Improvement";
  }

  if (String(item.status || "").toLowerCase() === "removed") {
    return "Regression";
  }

  return "Info";
}

function benchmarkComparisonCaseName(item) {
  return item.candidateName
    || item.baselineName
    || item.fixtureId
    || "benchmark case";
}

function benchmarkComparisonCaseFailed(item) {
  return item.candidatePassed === false;
}

function benchmarkComparisonCaseSkipped(item) {
  return Boolean(item.candidateSkipped || item.baselineSkipped);
}

function benchmarkComparisonPassPair(item) {
  return `${casePassText(item.baselinePassed, item.baselineSkipped)} -> ${casePassText(item.candidatePassed, item.candidateSkipped)}`;
}

function casePassText(value, skipped) {
  if (skipped) {
    return "SKIP";
  }

  return value == null ? "-" : value ? "PASS" : "FAIL";
}

function benchmarkComparisonQualityPair(item) {
  const baseline = item.baselineSkipped ? "SKIP" : item.baselineQualityGrade || "-";
  const candidate = item.candidateSkipped ? "SKIP" : item.candidateQualityGrade || "-";
  return `${baseline} -> ${candidate}`;
}

function benchmarkComparisonDurationPair(item) {
  return `${formatMilliseconds(item.baselineDurationMilliseconds)} -> ${formatMilliseconds(item.candidateDurationMilliseconds)}`;
}

function benchmarkComparisonSignalPair(signal) {
  if (!signal.baseline && !signal.candidate) {
    return "";
  }

  return `${signal.baseline || "-"} -> ${signal.candidate || "-"}`;
}

function benchmarkComparisonTopDelta(item) {
  const priority = new Set([
    "walls",
    "rooms",
    "roomClusters",
    "openings",
    "annotations",
    "annotationReferences",
    "objects",
    "objectGroups",
    "objectAggregates",
    "routingItems",
    "routingSuppressedObjects",
    "qualityIssues",
    "measurementChecked",
    "measurementOutliers",
    "failedAssertions"
  ]);
  const selected = (item.countDeltas ?? [])
    .filter((delta) => delta.delta != null && Number(delta.delta) !== 0)
    .sort((first, second) => {
      const firstPriority = priority.has(first.name) ? 0 : 1;
      const secondPriority = priority.has(second.name) ? 0 : 1;
      return firstPriority - secondPriority
        || Math.abs(Number(second.delta)) - Math.abs(Number(first.delta))
        || String(first.name).localeCompare(String(second.name));
    })
    .slice(0, 4)
    .map((delta) => `${delta.name} ${formatSigned(delta.delta)}`);
  return selected.join(", ");
}

function describeBenchmarkComparisonCase(item) {
  return {
    type: "benchmark comparison case",
    id: item.fixtureId,
    kind: `${item.status} / ${benchmarkComparisonCaseSeverity(item)}`,
    confidence: item.candidateQualityConfidence ?? item.baselineQualityConfidence ?? null,
    metadata: [
      `pass ${benchmarkComparisonPassPair(item)}`,
      `quality ${benchmarkComparisonQualityPair(item)}`,
      `failed assertions ${intPair(item.baselineFailedAssertionCount, item.candidateFailedAssertionCount)}`,
      `duration ${benchmarkComparisonDurationPair(item)}`
    ].join(" | "),
    reviewReasons: (item.signals ?? []).map((signal) => `${signal.severity}: ${signal.code} - ${signal.message}`),
    evidence: [
      item.baselineSkipReason ? `Baseline skipped: ${item.baselineSkipReason}` : "",
      item.candidateSkipReason ? `Candidate skipped: ${item.candidateSkipReason}` : "",
      ...((item.countDeltas ?? [])
        .filter((delta) => delta.delta != null && Number(delta.delta) !== 0)
        .slice(0, 12)
        .map((delta) => `${delta.name}: ${delta.baseline ?? "-"} -> ${delta.candidate ?? "-"} (${formatSigned(delta.delta)})`))
    ].filter(Boolean)
  };
}

function describeBenchmarkComparisonSignal(signal) {
  return {
    type: "benchmark comparison signal",
    id: signal.code,
    kind: signal.severity,
    metadata: benchmarkComparisonSignalPair(signal),
    reviewReasons: [signal.message].filter(Boolean),
    evidence: [
      signal.fixtureId ? `Fixture: ${signal.fixtureId}` : "",
      signal.baseline ? `Baseline: ${signal.baseline}` : "",
      signal.candidate ? `Candidate: ${signal.candidate}` : ""
    ].filter(Boolean)
  };
}

function renderBatchComparisonDetails(comparison) {
  const status = document.createElement("div");
  status.className = comparison.passed ? "compare-status clean" : "compare-status batch-regression";
  status.textContent = comparison.passed
    ? `PASS - ${comparison.items.length} batch item${comparison.items.length === 1 ? "" : "s"} compared`
    : `REGRESSION - ${comparison.regressionCount} regression signal${comparison.regressionCount === 1 ? "" : "s"}`;

  const list = document.createElement("dl");
  [
    ["Baseline", comparison.baselineOutputDirectory || "-"],
    ["Candidate", comparison.candidateOutputDirectory || "-"],
    ["Items", `${comparison.matchedItemCount} matched, ${comparison.addedItemCount} added, ${comparison.removedItemCount} removed`],
    ["Signals", `${comparison.regressionCount} regression / ${comparison.improvementCount} improvement / ${comparison.infoCount} info`],
    ["Diagnostics", `errors ${formatSigned(comparison.diagnosticErrorDelta)}, warnings ${formatSigned(comparison.diagnosticWarningDelta)}`],
    ["Visual", `issues ${formatSigned(comparison.visualIssueDelta)}, errors ${formatSigned(comparison.visualErrorIssueDelta)}`],
    ["Quality", formatSigned(comparison.qualityConfidenceAverageDelta, 3)],
    ["Duration", formatMilliseconds(comparison.totalDurationDeltaMilliseconds)],
    ["Generated", comparison.generatedAt || "-"]
  ].forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.compareDetails.append(status, list);

  const signals = comparison.signals
    .slice()
    .sort(compareBatchComparisonSignals)
    .slice(0, 18);
  elements.compareDetails.appendChild(renderBatchComparisonSignalList(signals));

  const items = comparison.items
    .slice()
    .sort(compareBatchComparisonItems)
    .slice(0, 80);
  elements.compareDetails.appendChild(renderBatchComparisonItemList(items));

  if (comparison.items.length > 80) {
    const overflow = document.createElement("p");
    overflow.className = "compare-note";
    overflow.textContent = `${comparison.items.length - 80} more compared items available in the JSON.`;
    elements.compareDetails.appendChild(overflow);
  }
}

function renderBatchComparisonSignalList(signals) {
  const wrapper = document.createElement("div");
  wrapper.className = "batch-comparison-list";

  const heading = document.createElement("strong");
  heading.className = "batch-comparison-heading";
  heading.textContent = "Top signals";
  wrapper.appendChild(heading);

  if (!signals.length) {
    const empty = document.createElement("p");
    empty.className = "compare-note";
    empty.textContent = "No regression or improvement signals.";
    wrapper.appendChild(empty);
    return wrapper;
  }

  signals.forEach((signal) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `batch-comparison-row signal ${signal.severity.toLowerCase()}`;

    const title = document.createElement("strong");
    title.textContent = signal.code;

    const message = document.createElement("span");
    message.textContent = signal.message || signal.key;

    const meta = document.createElement("small");
    meta.textContent = [signal.severity, signal.key, batchComparisonSignalPair(signal)].filter(Boolean).join(" / ");

    button.append(title, message, meta);
    button.addEventListener("click", () => {
      state.selectedItem = describeBatchComparisonSignal(signal);
      setSelection(state.selectedItem);
      refreshWorkspaceTabs();
    });
    wrapper.appendChild(button);
  });

  return wrapper;
}

function renderBatchComparisonItemList(items) {
  const wrapper = document.createElement("div");
  wrapper.className = "batch-comparison-list";

  const heading = document.createElement("strong");
  heading.className = "batch-comparison-heading";
  heading.textContent = "Compared items";
  wrapper.appendChild(heading);

  if (!items.length) {
    const empty = document.createElement("p");
    empty.className = "compare-note";
    empty.textContent = "No compared items.";
    wrapper.appendChild(empty);
    return wrapper;
  }

  items.forEach((item) => {
    const severity = batchComparisonItemSeverity(item).toLowerCase();
    const button = document.createElement("button");
    button.type = "button";
    button.className = `batch-comparison-row item ${String(item.status || "").toLowerCase()} ${severity}`;

    const title = document.createElement("strong");
    title.textContent = batchComparisonItemName(item);

    const metrics = document.createElement("span");
    metrics.textContent = [
      item.status,
      batchComparisonStatusPair(item),
      batchComparisonQualityPair(item),
      `visual ${intPair(item.baselineVisualIssueCount, item.candidateVisualIssueCount)}`
    ].filter(Boolean).join(" / ");

    const meta = document.createElement("small");
    meta.textContent = [
      `${item.signals.length} signal${item.signals.length === 1 ? "" : "s"}`,
      batchComparisonEvidenceSummary(item),
      batchComparisonTopDelta(item)
    ].filter(Boolean).join(" / ");

    button.append(title, metrics, meta);
    button.addEventListener("click", () => {
      state.selectedItem = describeBatchComparisonItem(item);
      setSelection(state.selectedItem);
      refreshWorkspaceTabs();
    });
    wrapper.appendChild(button);
  });

  return wrapper;
}

function compareBatchComparisonSignals(first, second) {
  return batchComparisonSeverityRank(first.severity) - batchComparisonSeverityRank(second.severity)
    || String(first.key).localeCompare(String(second.key))
    || String(first.code).localeCompare(String(second.code));
}

function compareBatchComparisonItems(first, second) {
  return batchComparisonSeverityRank(batchComparisonItemSeverity(first)) - batchComparisonSeverityRank(batchComparisonItemSeverity(second))
    || batchComparisonStatusRank(first.status) - batchComparisonStatusRank(second.status)
    || String(batchComparisonItemName(first)).localeCompare(batchComparisonItemName(second));
}

function batchComparisonSeverityRank(severity) {
  switch (String(severity || "").toLowerCase()) {
    case "regression":
      return 0;
    case "improvement":
      return 1;
    default:
      return 2;
  }
}

function batchComparisonStatusRank(status) {
  switch (String(status || "").toLowerCase()) {
    case "removed":
      return 0;
    case "added":
      return 1;
    default:
      return 2;
  }
}

function batchComparisonItemSeverity(item) {
  if (item.signals?.some((signal) => signal.severity === "Regression")) {
    return "Regression";
  }

  if (item.signals?.some((signal) => signal.severity === "Improvement")) {
    return "Improvement";
  }

  if (String(item.status || "").toLowerCase() === "removed") {
    return "Regression";
  }

  return "Info";
}

function batchComparisonItemName(item) {
  return item.candidateFileName
    || item.baselineFileName
    || item.candidateInputPath
    || item.baselineInputPath
    || item.key
    || "comparison item";
}

function batchComparisonStatusPair(item) {
  const baseline = item.baselineStatus || "-";
  const candidate = item.candidateStatus || "-";
  return `${baseline} -> ${candidate}`;
}

function batchComparisonQualityPair(item) {
  const baseline = item.baselineQualityGrade || "-";
  const candidate = item.candidateQualityGrade || "-";
  return `${baseline} -> ${candidate}`;
}

function intPair(baseline, candidate) {
  return `${baseline ?? "-"} -> ${candidate ?? "-"}`;
}

function batchComparisonSignalPair(signal) {
  if (!signal.baseline && !signal.candidate) {
    return "";
  }

  return `${signal.baseline || "-"} -> ${signal.candidate || "-"}`;
}

function batchComparisonTopDelta(item) {
  const selected = (item.deltas ?? [])
    .filter((delta) => delta.delta != null && Number(delta.delta) !== 0)
    .sort((first, second) => Math.abs(Number(second.delta)) - Math.abs(Number(first.delta)))
    .slice(0, 2)
    .map((delta) => `${delta.name} ${formatSigned(delta.delta)}`);
  return selected.join(", ");
}

function batchComparisonEvidenceSummary(item) {
  const baseline = evidenceSideSummary(item, "baseline");
  const candidate = evidenceSideSummary(item, "candidate");
  if (!baseline && !candidate) {
    return "no evidence paths";
  }

  return `${baseline || "-"} -> ${candidate || "-"}`;
}

function evidenceSideSummary(item, side) {
  const prefix = side === "baseline" ? "baseline" : "candidate";
  const parts = [];
  if (item[`${prefix}ScanJsonPath`]) {
    parts.push("scan");
  }
  if (item[`${prefix}VisualSnapshotPath`]) {
    parts.push("visual");
  }
  if (item[`${prefix}GeoJsonPath`]) {
    parts.push("geojson");
  }
  if (item[`${prefix}OverlayDirectory`]) {
    parts.push("svg");
  }
  return parts.join("+");
}

function batchComparisonItemHasEvidence(item) {
  return Boolean(
    item.baselineScanJsonPath
    || item.candidateScanJsonPath
    || item.baselineVisualSnapshotPath
    || item.candidateVisualSnapshotPath
    || item.baselineGeoJsonPath
    || item.candidateGeoJsonPath
    || item.baselineOverlayDirectory
    || item.candidateOverlayDirectory);
}

function batchComparisonEvidenceLines(item) {
  return [
    item.baselineScanJsonPath ? `Baseline scan: ${item.baselineScanJsonPath}` : "",
    item.candidateScanJsonPath ? `Candidate scan: ${item.candidateScanJsonPath}` : "",
    item.baselineVisualSnapshotPath ? `Baseline visual: ${item.baselineVisualSnapshotPath}` : "",
    item.candidateVisualSnapshotPath ? `Candidate visual: ${item.candidateVisualSnapshotPath}` : "",
    item.baselineGeoJsonPath ? `Baseline GeoJSON: ${item.baselineGeoJsonPath}` : "",
    item.candidateGeoJsonPath ? `Candidate GeoJSON: ${item.candidateGeoJsonPath}` : "",
    item.baselineOverlayDirectory ? `Baseline SVGs: ${item.baselineOverlayDirectory}` : "",
    item.candidateOverlayDirectory ? `Candidate SVGs: ${item.candidateOverlayDirectory}` : ""
  ].filter(Boolean);
}

function describeBatchComparisonItem(item) {
  return {
    type: "batch comparison item",
    id: item.key,
    kind: `${item.status} / ${batchComparisonItemSeverity(item)}`,
    confidence: item.candidateQualityConfidence ?? item.baselineQualityConfidence ?? null,
    metadata: [
      `status ${batchComparisonStatusPair(item)}`,
      `quality ${batchComparisonQualityPair(item)}`,
      `diagnostics ${intPair(item.baselineDiagnosticErrors, item.candidateDiagnosticErrors)}`,
      `visual ${intPair(item.baselineVisualIssueCount, item.candidateVisualIssueCount)}`,
      `duration ${formatMilliseconds(item.durationDeltaMilliseconds)}`
    ].join(" | "),
    reviewReasons: (item.signals ?? []).map((signal) => `${signal.severity}: ${signal.code} - ${signal.message}`),
    evidence: [
      ...batchComparisonEvidenceLines(item),
      ...(item.addedVisualIssueCodes ?? []).map((code) => `Added visual issue: ${code}`),
      ...(item.removedVisualIssueCodes ?? []).map((code) => `Removed visual issue: ${code}`),
      ...((item.deltas ?? [])
        .filter((delta) => delta.delta != null && Number(delta.delta) !== 0)
        .slice(0, 8)
        .map((delta) => `${delta.name}: ${delta.baseline ?? "-"} -> ${delta.candidate ?? "-"} (${formatSigned(delta.delta)})`))
    ]
  };
}

function describeBatchComparisonSignal(signal) {
  return {
    type: "batch comparison signal",
    id: signal.code,
    kind: signal.severity,
    metadata: batchComparisonSignalPair(signal),
    reviewReasons: [signal.message].filter(Boolean),
    evidence: [
      signal.key ? `Item: ${signal.key}` : "",
      signal.baseline ? `Baseline: ${signal.baseline}` : "",
      signal.candidate ? `Candidate: ${signal.candidate}` : ""
    ].filter(Boolean)
  };
}

function loadBenchmarkResultPayload(payload, label) {
  if (!isBenchmarkResult(payload)) {
    throw new Error("JSON is not an OpenPlanTrace benchmark result.");
  }

  beginBenchmarkOverlayLoad();
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.benchmarkResult = normalizeBenchmarkResult(payload, label);
  state.benchmarkManifest = null;
  state.benchmarkTargets = flattenBenchmarkReviewQueue(state.benchmarkResult);
  state.benchmarkReviewDecisions = new Map();
  state.benchmarkTargetEdits = new Map();
  state.benchmarkDeletedTargets = new Set();
  state.benchmarkAddedTargetSequence = 1;
  state.pendingBenchmarkReviewSession = null;
  state.benchmarkFilters = resetBenchmarkFilters();
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;
  state.enabledLayers.add("benchmarkTargets");
  syncLayerControls();
  state.selectedItem = null;

  setCounts(state.scan);
  setBenchmarkDetails();
  setSelection();
  setLegend();
  setAnalysisCounts(state.scan);
  drawOverlay();
  updateNavigation();
  setStatus("Benchmark result loaded");
}

function isBenchmarkResult(payload) {
  return payload?.schemaVersion === "openplantrace.benchmark-result.v1"
    || (Array.isArray(payload?.reviewQueue) && payload?.scoreboard);
}

function normalizeBenchmarkResult(payload, label) {
  return {
    schemaVersion: payload.schemaVersion || "openplantrace.benchmark-result.v1",
    name: payload.name || cleanUrlLabel(label) || "Benchmark result",
    label: cleanUrlLabel(label),
    generatedAt: payload.generatedAt || "",
    passed: payload.passed,
    caseCount: payload.caseCount ?? payload.cases?.length ?? 0,
    reviewQueueCount: payload.reviewQueueCount ?? payload.reviewQueue?.length ?? 0,
    passedCaseCount: payload.passedCaseCount ?? 0,
    failedCaseCount: payload.failedCaseCount ?? 0,
    skippedCaseCount: payload.skippedCaseCount ?? 0,
    passedAssertionCount: payload.passedAssertionCount ?? 0,
    failedAssertionCount: payload.failedAssertionCount ?? 0,
    scoreboard: payload.scoreboard || null,
    cases: Array.isArray(payload.cases) ? payload.cases : [],
    reviewQueue: Array.isArray(payload.reviewQueue) ? payload.reviewQueue : [],
    raw: payload
  };
}

function flattenBenchmarkReviewQueue(result) {
  return (result.reviewQueue ?? [])
    .map((item, itemIndex) => normalizeBenchmarkReviewQueueTarget(item, itemIndex))
    .filter(Boolean);
}

function normalizeBenchmarkReviewQueueTarget(item, itemIndex) {
  const detection = item?.detection ?? {};
  const detectorKey = benchmarkDetectorKeyFromResultDetector(item?.detector);
  const descriptor = benchmarkMetricDescriptors.find((entry) => entry.key === detectorKey)
    ?? { key: detectorKey, label: benchmarkDetectorLabel(detectorKey) };
  const fixtureId = item?.fixtureId || `fixture-${itemIndex + 1}`;
  const detectionId = detection.detectionId || `review-queue-${itemIndex + 1}`;
  const queueKind = benchmarkReviewQueueKind(item?.kind);
  const sourceLayers = normalizeStringArray(detection.sourceLayers);
  const sourcePrimitiveIds = normalizeStringArray(detection.sourcePrimitiveIds);
  const evidence = normalizeStringArray(detection.evidence);
  const detectedTags = normalizeStringArray(detection.detectedTags);
  const target = cleanBenchmarkTarget({
    id: detectionId,
    pageNumber: detection.pageNumber,
    bounds: detection.bounds,
    label: detection.label,
    text: detection.text,
    marker: detection.marker,
    minCount: detection.count,
    requiresReview: detection.requiresReview,
    objectCategory: detection.objectCategory || detection.category,
    objectKind: detection.objectKind || detection.kind,
    layerCategory: detection.layerCategory,
    routingSourceKind: detection.routingSourceKind,
    routingObstacleKind: detection.routingObstacleKind,
    routingInfluence: detection.routingInfluence,
    structuralInfluence: detection.structuralInfluence,
    roomUseKind: detection.roomUseKind,
    suppressesChildObjects: detection.suppressesChildObjects,
    detectedTags,
    confidence: detection.confidence,
    sourcePrimitiveIds,
    sourceLayers,
    evidence: [
      ...evidence,
      item?.recommendedAction ? `Recommended action: ${item.recommendedAction}` : "",
      `Benchmark result review queue kind: ${benchmarkReviewQueueKindLabel(queueKind)}.`
    ].filter(Boolean)
  });

  return normalizeBenchmarkTargetForState(target, {
    reviewKey: `queue|${fixtureId}|${detectorKey}|${detectionId}|${itemIndex}`,
    detectorKey,
    detectorLabel: descriptor.label,
    targetIndex: itemIndex,
    fixtureIndex: benchmarkResultFixtureIndex(fixtureId, item?.fixtureName),
    fixtureId,
    fixtureName: item?.fixtureName || fixtureId,
    sourcePath: item?.sourcePath || "",
    reviewQueueKind: queueKind,
    precisionScoringEnabled: Boolean(item?.precisionScoringEnabled),
    recommendedAction: item?.recommendedAction || "",
    isReviewQueueItem: true,
    queueIndex: itemIndex
  });
}

function benchmarkResultFixtureIndex(fixtureId, fixtureName) {
  const cases = state.benchmarkResult?.cases ?? [];
  const byId = cases.findIndex((item) => stringEquals(item.fixtureId, fixtureId));
  if (byId >= 0) {
    return byId;
  }

  const byName = cases.findIndex((item) => stringEquals(item.fixtureName, fixtureName));
  return byName >= 0 ? byName : 0;
}

function benchmarkDetectorKeyFromResultDetector(detector) {
  const key = String(detector || "").trim();
  if (!key) {
    return "objectGroupMetrics";
  }

  const normalized = key.replace(/[^a-z0-9]+/gi, "_").replace(/^_+|_+$/g, "").toLowerCase();
  return benchmarkDetectorKeyAliases[normalized] || `${normalized.replace(/_([a-z])/g, (_, value) => value.toUpperCase())}Metrics`;
}

function benchmarkReviewQueueKind(value) {
  switch (String(value || "").toLowerCase()) {
    case "precisionextra":
    case "precision_extra":
    case "precision-extra":
      return "PrecisionExtra";
    case "spotcheckextra":
    case "spot_check_extra":
    case "spot-check-extra":
      return "SpotCheckExtra";
    case "reviewonly":
    case "review_only":
    case "review-only":
      return "ReviewOnly";
    default:
      return "ReviewOnly";
  }
}

function benchmarkReviewQueueKindLabel(value) {
  switch (benchmarkReviewQueueKind(value)) {
    case "PrecisionExtra":
      return "Precision extra";
    case "SpotCheckExtra":
      return "Spot-check extra";
    case "ReviewOnly":
      return "Review-only";
    default:
      return "Review item";
  }
}

function benchmarkReviewQueueKindClass(value) {
  switch (benchmarkReviewQueueKind(value)) {
    case "PrecisionExtra":
      return "queue-precision-extra";
    case "SpotCheckExtra":
      return "queue-spot-check-extra";
    case "ReviewOnly":
      return "queue-review-only";
    default:
      return "queue-review-only";
  }
}

function loadBenchmarkManifestPayload(payload, label) {
  if (!isBenchmarkManifest(payload)) {
    throw new Error("JSON is not an OpenPlanTrace benchmark manifest.");
  }

  beginBenchmarkOverlayLoad();
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.benchmarkResult = null;
  state.benchmarkManifest = normalizeBenchmarkManifest(payload, label);
  state.benchmarkTargets = flattenBenchmarkTargets(state.benchmarkManifest);
  state.benchmarkReviewDecisions = seedBenchmarkReviewDecisions(state.benchmarkTargets);
  state.benchmarkTargetEdits = seedBenchmarkTargetEdits(state.benchmarkTargets);
  state.benchmarkDeletedTargets = new Set();
  state.benchmarkAddedTargetSequence = 1;
  state.benchmarkFilters = resetBenchmarkFilters();
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;

  if (state.pendingBenchmarkReviewSession) {
    try {
      const pending = state.pendingBenchmarkReviewSession;
      const result = applyBenchmarkReviewSession(pending.payload, pending.label);
      state.pendingBenchmarkReviewSession = null;
      setStatus(benchmarkReviewSessionStatus(result));
      return;
    } catch (error) {
      const message = error.message || String(error);
      state.pendingBenchmarkReviewSession = null;
      setBenchmarkDetails(state.benchmarkManifest, activeBenchmarkTargets(), message);
      drawOverlay();
      setStatus("Review session failed");
      return;
    }
  }

  setBenchmarkDetails();
  drawOverlay();
  setStatus("Ready");
}

function isBenchmarkManifest(payload) {
  return payload?.schemaVersion === "openplantrace.benchmark-manifest.v1"
    || (Array.isArray(payload?.fixtures) && payload.fixtures.some((fixture) => fixture?.expectations));
}

function isBenchmarkReviewSession(payload) {
  return payload?.schemaVersion === "openplantrace.viewer-benchmark-review-session.v1"
    || (payload?.manifest
      && payload?.summary
      && (Array.isArray(payload?.decisions)
        || Array.isArray(payload?.boundsEdits)
        || Array.isArray(payload?.addedTargets)
        || Array.isArray(payload?.deletedTargets)));
}

function loadBenchmarkReviewSessionPayload(payload, label) {
  if (!isBenchmarkReviewSession(payload)) {
    throw new Error("JSON is not an OpenPlanTrace benchmark review session.");
  }

  beginBenchmarkOverlayLoad();
  state.benchmarkComparison = null;
  state.batchComparison = null;
  if (!state.benchmarkManifest) {
    state.pendingBenchmarkReviewSession = {
      payload: clonePlain(payload),
      label: cleanUrlLabel(label)
    };
    setBenchmarkDetails(null, [], "Review session loaded. Load its benchmark manifest to apply decisions, edits, and added targets.");
    setStatus("Review session pending");
    return;
  }

  const result = applyBenchmarkReviewSession(payload, label);
  state.pendingBenchmarkReviewSession = null;
  setStatus(benchmarkReviewSessionStatus(result));
}

function applyBenchmarkReviewSession(payload, label) {
  if (!state.benchmarkManifest) {
    throw new Error("Load a benchmark manifest before applying a review session.");
  }

  const result = {
    label: cleanUrlLabel(label),
    decisions: 0,
    boundsEdits: 0,
    addedTargets: 0,
    deletedTargets: 0,
    skipped: 0
  };

  addBenchmarkReviewSessionTargets(payload, result);

  const targetByReviewKey = () => new Map(state.benchmarkTargets.map((target) => [target.reviewKey, target]));

  for (const decisionTarget of Array.isArray(payload.decisions) ? payload.decisions : []) {
    const reviewKey = decisionTarget?.reviewKey;
    const decision = normalizeBenchmarkReviewDecision(decisionTarget?.decision);
    if (!reviewKey || !decision || !targetByReviewKey().has(reviewKey)) {
      result.skipped++;
      continue;
    }

    state.benchmarkReviewDecisions.set(reviewKey, {
      decision,
      reviewedAt: decisionTarget.reviewedAt || payload.exportedAt || new Date().toISOString()
    });
    result.decisions++;
  }

  for (const edit of Array.isArray(payload.boundsEdits) ? payload.boundsEdits : []) {
    const reviewKey = edit?.reviewKey;
    const target = reviewKey ? targetByReviewKey().get(reviewKey) : null;
    if (!target) {
      result.skipped++;
      continue;
    }

    const bounds = normalizeRect(edit.bounds);
    const pageNumber = normalizedPageNumber(edit.pageNumber);
    target.bounds = bounds;
    target.pageNumber = pageNumber;

    if (targetBoundsMatchOriginal(target)) {
      state.benchmarkTargetEdits.delete(reviewKey);
    } else {
      state.benchmarkTargetEdits.set(reviewKey, {
        pageNumber,
        bounds: clonePlain(bounds),
        editedAt: edit.editedAt || payload.exportedAt || new Date().toISOString()
      });
    }

    result.boundsEdits++;
  }

  for (const deletedTarget of Array.isArray(payload.deletedTargets) ? payload.deletedTargets : []) {
    const reviewKey = deletedTarget?.reviewKey;
    const target = reviewKey ? targetByReviewKey().get(reviewKey) : null;
    if (!target) {
      result.skipped++;
      continue;
    }

    if (target.isAdded) {
      state.benchmarkTargets = state.benchmarkTargets.filter((item) => item.reviewKey !== reviewKey);
    } else {
      state.benchmarkDeletedTargets.add(reviewKey);
    }

    state.benchmarkReviewDecisions.delete(reviewKey);
    state.benchmarkTargetEdits.delete(reviewKey);
    result.deletedTargets++;
  }

  applyBenchmarkReviewSessionFilters(payload.summary?.filters);
  state.selectedItem = null;
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;
  refreshBenchmarkReviewUi();
  return result;
}

function addBenchmarkReviewSessionTargets(payload, result) {
  const candidates = [
    ...(Array.isArray(payload.addedTargets) ? payload.addedTargets : []),
    ...(Array.isArray(payload.decisions) ? payload.decisions.filter((target) => target?.isAdded) : [])
  ];
  const seen = new Set();

  for (const candidate of candidates) {
    const reviewKey = candidate?.reviewKey || candidate?.target?.reviewKey;
    const dedupeKey = reviewKey || `${candidate?.fixtureId || ""}|${candidate?.detectorKey || ""}|${candidate?.id || ""}`;
    if (!dedupeKey || seen.has(dedupeKey)) {
      continue;
    }

    seen.add(dedupeKey);
    const added = normalizeBenchmarkReviewSessionAddedTarget(candidate, payload.exportedAt);
    if (!added) {
      result.skipped++;
      continue;
    }

    if (state.benchmarkTargets.some((target) => target.reviewKey === added.reviewKey)) {
      continue;
    }

    state.benchmarkTargets.push(added);
    result.addedTargets++;
  }
}

function normalizeBenchmarkReviewSessionAddedTarget(candidate, exportedAt) {
  const summary = candidate?.target?.reviewKey ? candidate.target : candidate;
  const manifestTarget = candidate?.manifestTarget || candidate?.target?.manifestTarget || candidate;
  const detectorKey = summary?.detectorKey || candidate?.detectorKey || manifestTarget?.detectorKey;
  const descriptor = benchmarkMetricDescriptors.find((entry) => entry.key === detectorKey);
  if (!descriptor) {
    return null;
  }

  const fixtureIndex = resolveBenchmarkSessionFixtureIndex(summary?.fixtureId || candidate?.fixtureId, summary?.fixtureName || candidate?.fixtureName);
  const fixture = state.benchmarkManifest.fixtures?.[fixtureIndex];
  if (!fixture) {
    return null;
  }

  const sequence = state.benchmarkAddedTargetSequence++;
  const id = summary?.id || manifestTarget?.id || uniqueBenchmarkTargetId(detectorKey, "session-target");
  const reviewKey = summary?.reviewKey || candidate?.reviewKey || `added|${fixture.id || `fixture-${fixtureIndex + 1}`}|${detectorKey}|${id}|${sequence}`;
  const xReview = manifestTarget?.xReview || candidate?.xReview || {};
  const targetShape = cleanBenchmarkTarget({
    ...manifestTarget,
    id,
    pageNumber: summary?.pageNumber ?? manifestTarget?.pageNumber,
    bounds: clonePlain(summary?.bounds ?? manifestTarget?.bounds),
    confidence: summary?.confidence ?? manifestTarget?.confidence,
    sourcePrimitiveIds: summary?.sourcePrimitiveIds ?? manifestTarget?.sourcePrimitiveIds,
    sourceLayers: summary?.sourceLayers ?? manifestTarget?.sourceLayers,
    evidence: summary?.evidence ?? manifestTarget?.evidence
  });

  return normalizeBenchmarkTargetForState(targetShape, {
    id,
    reviewKey,
    detectorKey,
    detectorLabel: descriptor.label,
    targetIndex: Number.MAX_SAFE_INTEGER - sequence,
    fixtureIndex,
    fixtureId: fixture.id || `fixture-${fixtureIndex + 1}`,
    fixtureName: fixture.name || fixture.id || `fixture-${fixtureIndex + 1}`,
    sourcePath: fixture.sourcePath || "",
    isAdded: true,
    addedAt: xReview.createdAt || exportedAt || new Date().toISOString()
  });
}

function resolveBenchmarkSessionFixtureIndex(fixtureId, fixtureName) {
  const fixtures = state.benchmarkManifest?.fixtures ?? [];
  const byId = fixtures.findIndex((fixture, index) =>
    stringEquals(fixture.id || `fixture-${index + 1}`, fixtureId));
  if (byId >= 0) {
    return byId;
  }

  const byName = fixtures.findIndex((fixture) => stringEquals(fixture.name, fixtureName));
  return byName >= 0 ? byName : 0;
}

function applyBenchmarkReviewSessionFilters(filters) {
  if (!filters || typeof filters !== "object") {
    return;
  }

  const detectorKeys = new Set(["all", ...benchmarkMetricDescriptors.map((entry) => entry.key)]);
  const queueKinds = new Set(["all", "PrecisionExtra", "SpotCheckExtra", "ReviewOnly"]);
  const statuses = new Set(["all", "unreviewed", "accepted", "rejected", "needsReview"]);
  const issues = new Set(["all", "missingBounds", "lowConfidence", "missingEvidence", "boundsEdited", "added"]);
  const pages = new Set(["all", "current", "unpaged"]);
  state.benchmarkFilters = {
    query: cleanManualText(filters.query) || "",
    detector: detectorKeys.has(filters.detector) ? filters.detector : "all",
    queueKind: queueKinds.has(filters.queueKind) ? filters.queueKind : "all",
    status: statuses.has(filters.status) ? filters.status : "all",
    issue: issues.has(filters.issue) ? filters.issue : "all",
    page: pages.has(filters.page) ? filters.page : "all"
  };
}

function benchmarkReviewSessionStatus(result) {
  const parts = [
    `${result.decisions} decision${result.decisions === 1 ? "" : "s"}`,
    `${result.boundsEdits} bound edit${result.boundsEdits === 1 ? "" : "s"}`,
    `${result.addedTargets} added`,
    `${result.deletedTargets} deleted`
  ];

  if (result.skipped) {
    parts.push(`${result.skipped} skipped`);
  }

  return `Review session loaded: ${parts.join(", ")}`;
}

function stringEquals(left, right) {
  return String(left || "").trim().toLowerCase() === String(right || "").trim().toLowerCase();
}

function normalizeBenchmarkManifest(payload, label) {
  return {
    schemaVersion: payload.schemaVersion || "openplantrace.benchmark-manifest.v1",
    name: payload.name || cleanUrlLabel(label) || "Benchmark manifest",
    label: cleanUrlLabel(label),
    raw: payload,
    fixtures: Array.isArray(payload.fixtures) ? payload.fixtures : []
  };
}

const objectCategoryOptions = [
  "",
  "TextLabel",
  "GenericSymbol",
  "Fixture",
  "Furniture",
  "Vehicle",
  "Stair",
  "Elevator",
  "Column",
  "Shaft",
  "PlumbingFixture",
  "ElectricalDevice",
  "Lighting",
  "HVACEquipment",
  "FireSafety",
  "Equipment",
  "Structural"
];

const objectKindOptions = ["", "Unknown", "Fixture", "Furniture", "Vehicle", "Symbol", "Stair", "TextLabel"];
const routingSourceKindOptions = ["", "Wall", "Opening", "Room", "ObjectCandidate", "ObjectAggregate"];
const routingObstacleKindOptions = ["", "SoftObstacle", "HardObstacle", "StructuralBarrier"];
const routingInfluenceOptions = ["", "Ignore", "RoomUseEvidenceOnly", "SoftObstacle", "HardObstacle", "StructuralBarrier"];
const structuralInfluenceOptions = ["", "None", "NonStructural", "FixedEquipment", "Structural"];
const routingSuppressionReasonOptions = ["", "ReplacedByObjectAggregate", "AggregateRoomUseEvidenceOnly"];
const routingSuppressionActionOptions = ["", "IgnoreForRouting", "UseAggregateObstacle", "UseAggregateRoomUseHint"];
const roomUseKindOptions = [
  "",
  "Office",
  "Meeting",
  "Corridor",
  "Lobby",
  "Restroom",
  "Bathroom",
  "Storage",
  "Mechanical",
  "Electrical",
  "Plumbing",
  "HVAC",
  "Utility",
  "Kitchen",
  "Living",
  "Bedroom",
  "Stair",
  "Elevator",
  "Shaft",
  "Laboratory",
  "Industrial",
  "Parking",
  "CommonArea"
];

const benchmarkMetricDescriptors = [
  { key: "regionMetrics", label: "Region targets" },
  { key: "dimensionMetrics", label: "Dimension targets" },
  { key: "annotationMetrics", label: "Annotation targets" },
  { key: "annotationReferenceMetrics", label: "Annotation reference targets" },
  { key: "gridAxisMetrics", label: "Grid axis targets" },
  { key: "wallMetrics", label: "Wall targets" },
  { key: "roomMetrics", label: "Room targets" },
  { key: "openingMetrics", label: "Opening targets" },
  { key: "objectMetrics", label: "Object targets" },
  { key: "objectGroupMetrics", label: "Object group targets" },
  { key: "objectAggregateMetrics", label: "Object aggregate targets" },
  { key: "routingBarrierMetrics", label: "Routing barrier targets" },
  { key: "routingPassageMetrics", label: "Routing passage targets" },
  { key: "routingObstacleMetrics", label: "Routing obstacle targets" },
  { key: "routingRoomUseHintMetrics", label: "Routing room-use targets" },
  { key: "routingSuppressedObjectMetrics", label: "Routing suppressed-object targets" },
  { key: "layerMetrics", label: "Layer targets" }
];

const benchmarkDetectorKeyAliases = {
  regions: "regionMetrics",
  region: "regionMetrics",
  dimensions: "dimensionMetrics",
  dimension: "dimensionMetrics",
  annotations: "annotationMetrics",
  annotation: "annotationMetrics",
  annotation_references: "annotationReferenceMetrics",
  annotation_reference: "annotationReferenceMetrics",
  grid_axes: "gridAxisMetrics",
  grid_axis: "gridAxisMetrics",
  walls: "wallMetrics",
  wall: "wallMetrics",
  rooms: "roomMetrics",
  room: "roomMetrics",
  openings: "openingMetrics",
  opening: "openingMetrics",
  objects: "objectMetrics",
  object: "objectMetrics",
  object_groups: "objectGroupMetrics",
  object_group: "objectGroupMetrics",
  object_aggregates: "objectAggregateMetrics",
  object_aggregate: "objectAggregateMetrics",
  routing_barriers: "routingBarrierMetrics",
  routing_barrier: "routingBarrierMetrics",
  routing_passages: "routingPassageMetrics",
  routing_passage: "routingPassageMetrics",
  routing_obstacles: "routingObstacleMetrics",
  routing_obstacle: "routingObstacleMetrics",
  routing_room_use_hints: "routingRoomUseHintMetrics",
  routing_room_use_hint: "routingRoomUseHintMetrics",
  routing_suppressed_objects: "routingSuppressedObjectMetrics",
  routing_suppressed_object: "routingSuppressedObjectMetrics",
  layers: "layerMetrics",
  layer: "layerMetrics"
};

const manualBenchmarkDetectorKeys = [
  "regionMetrics",
  "dimensionMetrics",
  "annotationMetrics",
  "annotationReferenceMetrics",
  "gridAxisMetrics",
  "wallMetrics",
  "roomMetrics",
  "openingMetrics",
  "objectMetrics",
  "objectGroupMetrics",
  "objectAggregateMetrics",
  "routingBarrierMetrics",
  "routingPassageMetrics",
  "routingObstacleMetrics",
  "routingRoomUseHintMetrics",
  "routingSuppressedObjectMetrics"
];

const manualBenchmarkFieldOptions = {
  regionMetrics: {
    kindLabel: "Region",
    kindKey: "regionKind",
    kindOptions: ["", "Sheet", "MainFloorPlan", "TitleBlock", "Notes", "Dimensions", "KeyPlan", "Legend"]
  },
  dimensionMetrics: {
    kindLabel: "Kind",
    kindKey: "dimensionKind",
    kindOptions: ["", "Linear"],
    subtypeLabel: "Orient",
    subtypeKey: "dimensionOrientation",
    subtypeOptions: ["", "Horizontal", "Vertical", "Aligned"]
  },
  annotationMetrics: {
    kindLabel: "Annotation",
    kindKey: "annotationKind",
    kindOptions: ["", "GeneralNotes", "Keynotes", "Legend", "Schedule", "RevisionTable", "Callouts", "TextBlock"]
  },
  annotationReferenceMetrics: {
    kindLabel: "Annotation",
    kindKey: "annotationKind",
    kindOptions: ["", "GeneralNotes", "Keynotes", "Legend", "Schedule", "RevisionTable", "Callouts", "TextBlock"]
  },
  gridAxisMetrics: {
    kindLabel: "Orient",
    kindKey: "gridAxisOrientation",
    kindOptions: ["", "Horizontal", "Vertical"]
  },
  openingMetrics: {
    kindLabel: "Opening",
    kindKey: "openingType",
    kindOptions: ["", "GenericOpening", "Door", "Window"],
    subtypeLabel: "Operation",
    subtypeKey: "openingOperation",
    subtypeOptions: ["", "PassThrough", "Hinged", "DoubleSwing", "Sliding", "PocketSliding", "Fixed"]
  },
  objectMetrics: {
    kindLabel: "Object",
    kindKey: "objectCategory",
    kindOptions: objectCategoryOptions,
    subtypeLabel: "Kind",
    subtypeKey: "objectKind",
    subtypeOptions: objectKindOptions
  },
  objectGroupMetrics: {
    kindLabel: "Object",
    kindKey: "objectCategory",
    kindOptions: objectCategoryOptions,
    subtypeLabel: "Kind",
    subtypeKey: "objectKind",
    subtypeOptions: objectKindOptions
  },
  objectAggregateMetrics: {
    kindLabel: "Object",
    kindKey: "objectCategory",
    kindOptions: objectCategoryOptions,
    subtypeLabel: "Kind",
    subtypeKey: "objectKind",
    subtypeOptions: objectKindOptions,
    extraFields: [
      { label: "Routing", key: "routingInfluence", options: routingInfluenceOptions },
      { label: "Structure", key: "structuralInfluence", options: structuralInfluenceOptions },
      { label: "Room use", key: "roomUseKind", options: roomUseKindOptions }
    ]
  },
  routingBarrierMetrics: {
    kindLabel: "Source",
    kindKey: "routingSourceKind",
    kindOptions: routingSourceKindOptions
  },
  routingPassageMetrics: {
    kindLabel: "Source",
    kindKey: "routingSourceKind",
    kindOptions: routingSourceKindOptions,
    subtypeLabel: "Opening",
    subtypeKey: "openingType",
    subtypeOptions: ["", "GenericOpening", "Door", "Window"],
    extraFields: [
      { label: "Operation", key: "openingOperation", options: ["", "PassThrough", "Hinged", "DoubleSwing", "Sliding", "PocketSliding", "Fixed"] }
    ]
  },
  routingObstacleMetrics: {
    kindLabel: "Obstacle",
    kindKey: "routingObstacleKind",
    kindOptions: routingObstacleKindOptions,
    subtypeLabel: "Source",
    subtypeKey: "routingSourceKind",
    subtypeOptions: routingSourceKindOptions,
    extraFields: [
      { label: "Object", key: "objectCategory", options: objectCategoryOptions },
      { label: "Obj kind", key: "objectKind", options: objectKindOptions },
      { label: "Routing", key: "routingInfluence", options: routingInfluenceOptions },
      { label: "Structure", key: "structuralInfluence", options: structuralInfluenceOptions }
    ]
  },
  routingRoomUseHintMetrics: {
    kindLabel: "Source",
    kindKey: "routingSourceKind",
    kindOptions: routingSourceKindOptions,
    subtypeLabel: "Room use",
    subtypeKey: "roomUseKind",
    subtypeOptions: roomUseKindOptions
  },
  routingSuppressedObjectMetrics: {
    kindLabel: "Reason",
    kindKey: "suppressionReason",
    kindOptions: routingSuppressionReasonOptions,
    subtypeLabel: "Action",
    subtypeKey: "suppressionAction",
    subtypeOptions: routingSuppressionActionOptions,
    extraFields: [
      { label: "Object", key: "objectCategory", options: objectCategoryOptions },
      { label: "Obj kind", key: "objectKind", options: objectKindOptions },
      { label: "Routing", key: "routingInfluence", options: routingInfluenceOptions },
      { label: "Structure", key: "structuralInfluence", options: structuralInfluenceOptions }
    ]
  }
};

function flattenBenchmarkTargets(manifest) {
  return (manifest.fixtures ?? []).flatMap((fixture, fixtureIndex) => {
    const fixtureId = fixture.id || `fixture-${fixtureIndex + 1}`;
    const expectations = fixture.expectations ?? {};
    fixture.expectations = expectations;

    return benchmarkMetricDescriptors.flatMap((descriptor) => {
      const metric = expectations[descriptor.key];
      const targets = Array.isArray(metric?.targets) ? metric.targets : [];

      return targets.map((target, targetIndex) => normalizeBenchmarkTargetForState(target, {
        id: target.id || `${fixtureId}:${descriptor.key}:${targetIndex + 1}`,
        reviewKey: benchmarkReviewKey(fixtureId, fixtureIndex, descriptor.key, target.id, targetIndex),
        detectorKey: descriptor.key,
        detectorLabel: descriptor.label,
        targetIndex,
        fixtureIndex,
        fixtureId,
        fixtureName: fixture.name || fixtureId,
        sourcePath: fixture.sourcePath || "",
        fixtureProperties: fixture.properties ?? {},
        minRecall: metric?.minRecall,
        minPrecision: metric?.minPrecision
      }));
    });
  });
}

function normalizeBenchmarkTargetForState(target, metadata) {
  const pageNumber = normalizedPageNumber(target.pageNumber);
  const bounds = normalizeRect(target.bounds);
  return {
    ...target,
    ...metadata,
    id: metadata.id || target.id,
    pageNumber,
    bounds,
    originalPageNumber: pageNumber,
    originalBounds: clonePlain(bounds),
    sourcePrimitiveIds: normalizeStringArray(target.sourcePrimitiveIds),
    sourceLayers: normalizeStringArray(target.sourceLayers),
    detectedTags: normalizeStringArray(target.detectedTags),
    evidence: normalizeStringArray(target.evidence)
  };
}

function benchmarkReviewKey(fixtureId, fixtureIndex, detectorKey, targetId, targetIndex) {
  return `${fixtureId || `fixture-${fixtureIndex + 1}`}|${fixtureIndex}|${detectorKey}|${targetId || ""}|${targetIndex}`;
}

function seedBenchmarkReviewDecisions(targets) {
  const decisions = new Map();
  targets.forEach((target) => {
    const review = target.xReview ?? target.review;
    const decision = normalizeBenchmarkReviewDecision(review?.decision);
    if (decision) {
      decisions.set(target.reviewKey, {
        decision,
        reviewedAt: review.reviewedAt || review.updatedAt || new Date().toISOString()
      });
    }
  });

  return decisions;
}

function seedBenchmarkTargetEdits(targets) {
  const edits = new Map();
  targets.forEach((target) => {
    const review = target.xReview ?? target.review;
    if (review?.boundsEdited) {
      edits.set(target.reviewKey, {
        pageNumber: target.pageNumber,
        bounds: clonePlain(target.bounds),
        editedAt: review.boundsEditedAt || review.updatedAt || review.reviewedAt || new Date().toISOString()
      });
    }
  });

  return edits;
}

function normalizeBenchmarkReviewDecision(value) {
  switch (String(value || "").toLowerCase()) {
    case "accepted":
    case "accept":
      return "accepted";
    case "rejected":
    case "reject":
      return "rejected";
    case "needsreview":
    case "needs-review":
    case "review":
      return "needsReview";
    default:
      return "";
  }
}

function normalizedPageNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : null;
}

function normalizeRect(bounds) {
  if (!bounds || typeof bounds !== "object") {
    return null;
  }

  let x = Number(bounds.x);
  let y = Number(bounds.y);
  let width = Number(bounds.width);
  let height = Number(bounds.height);
  if (![x, y, width, height].every(Number.isFinite)) {
    return null;
  }

  if (width < 0) {
    x += width;
    width = Math.abs(width);
  }

  if (height < 0) {
    y += height;
    height = Math.abs(height);
  }

  return { x, y, width, height };
}

function normalizePoint(point) {
  if (!point || typeof point !== "object") {
    return null;
  }

  const x = Number(point.x);
  const y = Number(point.y);
  return Number.isFinite(x) && Number.isFinite(y)
    ? { x, y }
    : null;
}

function normalizeLine(line) {
  if (!line || typeof line !== "object") {
    return null;
  }

  const start = normalizePoint(line.start);
  const end = normalizePoint(line.end);
  return start && end ? { start, end } : null;
}

function setBenchmarkDetails(manifest = state.benchmarkManifest, targets = activeBenchmarkTargets(), errorMessage = "") {
  elements.benchmarkDetails.replaceChildren();
  const benchmarkResult = state.benchmarkResult;

  if (errorMessage) {
    const error = document.createElement("div");
    error.className = "benchmark-status error";
    error.textContent = errorMessage;
    elements.benchmarkDetails.appendChild(error);
    refreshWorkspaceTabs();
    return;
  }

  if (!manifest && !benchmarkResult) {
    elements.benchmarkDetails.textContent = "No benchmark review data";
    refreshWorkspaceTabs();
    return;
  }

  const filteredTargets = filteredBenchmarkTargets(targets);
  const drawable = targets.filter((target) => target.bounds).length;
  const missingBounds = targets.length - drawable;
  const missingEvidence = targets.filter((target) => !benchmarkTargetHasEvidence(target)).length;
  const lowConfidence = targets.filter(isLowConfidenceBenchmarkTarget).length;
  const boundsEdited = targets.filter(benchmarkTargetBoundsEdited).length;
  const added = targets.filter((target) => target.isAdded).length;
  const deleted = state.benchmarkDeletedTargets.size;
  const reviewSummary = benchmarkReviewSummary(targets);

  const status = document.createElement("div");
  status.className = [
    "benchmark-status",
    reviewSummary.rejected || reviewSummary.needsReview || missingBounds || missingEvidence || lowConfidence ? "review" : "clean"
  ].filter(Boolean).join(" ");
  status.textContent = benchmarkResult
    ? `${targets.length} queue item${targets.length === 1 ? "" : "s"} loaded - ${reviewSummary.reviewed} reviewed - ${filteredTargets.length} shown`
    : `${targets.length} target${targets.length === 1 ? "" : "s"} loaded - ${reviewSummary.reviewed} reviewed - ${filteredTargets.length} shown`;

  const list = document.createElement("dl");
  const rows = benchmarkResult
    ? benchmarkResultRows(benchmarkResult, filteredTargets)
    : [
    ["Name", manifest.name || manifest.label || "-"],
    ["Fixtures", `${manifest.fixtures?.length ?? 0}`],
    ["Shown", `${filteredTargets.length}`],
    ["Drawable", `${drawable}`],
    ["Accepted", `${reviewSummary.accepted}`],
    ["Rejected", `${reviewSummary.rejected}`],
    ["Needs review", `${reviewSummary.needsReview}`],
    ["Unreviewed", `${reviewSummary.unreviewed}`],
    ["Added", `${added}`],
    ["Deleted", `${deleted}`],
    ["Bounds edits", `${boundsEdited}`],
    ["No bounds", `${missingBounds}`],
    ["Low conf", `${lowConfidence}`],
    ["No evidence", `${missingEvidence}`]
  ];

  rows.forEach(([label, value]) => {
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = label;
    detail.textContent = value;
    list.append(term, detail);
  });

  elements.benchmarkDetails.append(
    status,
    benchmarkResult
      ? renderBenchmarkResultReviewToolbar(targets, filteredTargets)
      : renderBenchmarkReviewToolbar(targets, filteredTargets),
    ...(benchmarkResult ? [] : [renderBenchmarkManualTargetPanel()]),
    renderBenchmarkFilterControls(targets, filteredTargets),
    list);

  if (benchmarkResult) {
    const queueCounts = benchmarkReviewQueueKindCounts(filteredTargets);
    if (queueCounts.length) {
      const counts = document.createElement("div");
      counts.className = "benchmark-counts";
      queueCounts.forEach(([kind, count]) => {
        const item = document.createElement("div");
        item.className = `benchmark-count ${benchmarkReviewQueueKindClass(kind)}`;
        item.textContent = `${benchmarkReviewQueueKindLabel(kind)}: ${count}`;
        counts.appendChild(item);
      });
      elements.benchmarkDetails.appendChild(counts);
    }
  }

  const detectorCounts = benchmarkDetectorCounts(filteredTargets);
  if (detectorCounts.length) {
    const counts = document.createElement("div");
    counts.className = "benchmark-counts";
    detectorCounts.forEach(([label, count]) => {
      const item = document.createElement("div");
      item.className = "benchmark-count";
      item.textContent = `${label}: ${count}`;
      counts.appendChild(item);
    });
    elements.benchmarkDetails.appendChild(counts);
  }

  const targetList = document.createElement("div");
  targetList.className = "benchmark-target-list";
  filteredTargets
    .slice()
    .sort(compareBenchmarkTargets)
    .slice(0, 80)
    .forEach((target) => targetList.appendChild(renderBenchmarkTargetButton(target)));

  if (targetList.childElementCount > 0) {
    elements.benchmarkDetails.appendChild(targetList);
  } else if (targets.length) {
    const empty = document.createElement("small");
    empty.className = "benchmark-overflow";
    empty.textContent = "No targets match the current filters";
    elements.benchmarkDetails.appendChild(empty);
  }

  if (filteredTargets.length > 80) {
    const overflow = document.createElement("small");
    overflow.className = "benchmark-overflow";
    overflow.textContent = `${filteredTargets.length - 80} more filtered targets available in manifest JSON`;
    elements.benchmarkDetails.appendChild(overflow);
  }
  refreshWorkspaceTabs();
}

function benchmarkResultRows(result, filteredTargets) {
  const targets = activeBenchmarkTargets();
  const reviewSummary = benchmarkReviewSummary(targets);
  const scoreboard = result.scoreboard ?? {};
  return [
    ["Name", result.name || result.label || "-"],
    ["Grade", scoreboard.grade || "-"],
    ["Score", scoreboard.overallScore == null ? "-" : formatNumber(scoreboard.overallScore)],
    ["Downstream", scoreboard.consumerReadinessScore == null ? "-" : formatNumber(scoreboard.consumerReadinessScore)],
    ["Ready", scoreboard.readyForDownstreamUse == null ? "-" : (scoreboard.readyForDownstreamUse ? "Yes" : "No")],
    ["Cases", `${result.caseCount ?? result.cases?.length ?? 0}`],
    ["Failed cases", `${result.failedCaseCount ?? 0}`],
    ["Failed asserts", `${result.failedAssertionCount ?? 0}`],
    ["Queue", `${targets.length}`],
    ["Shown", `${filteredTargets.length}`],
    ["Accepted", `${reviewSummary.accepted}`],
    ["Rejected", `${reviewSummary.rejected}`],
    ["Needs review", `${reviewSummary.needsReview}`],
    ["Unreviewed", `${reviewSummary.unreviewed}`],
    ["Generated", result.generatedAt || "-"]
  ];
}

function renderBenchmarkResultReviewToolbar(targets, filteredTargets = targets) {
  const toolbar = document.createElement("div");
  toolbar.className = "benchmark-review-toolbar";

  const acceptShown = document.createElement("button");
  acceptShown.type = "button";
  acceptShown.textContent = "Accept shown";
  acceptShown.disabled = !filteredTargets.some((target) => !benchmarkTargetDecision(target));
  acceptShown.addEventListener("click", () => setBenchmarkDecisionForTargets(filteredTargets, "accepted"));

  const reviewShown = document.createElement("button");
  reviewShown.type = "button";
  reviewShown.textContent = "Review shown";
  reviewShown.disabled = !filteredTargets.some((target) => benchmarkTargetDecision(target) !== "needsReview");
  reviewShown.addEventListener("click", () => setBenchmarkDecisionForTargets(filteredTargets, "needsReview"));

  const reset = document.createElement("button");
  reset.type = "button";
  reset.textContent = "Reset queue";
  reset.disabled = state.benchmarkReviewDecisions.size === 0
    && state.benchmarkTargetEdits.size === 0
    && state.benchmarkDeletedTargets.size === 0
    && !benchmarkFiltersActive();
  reset.addEventListener("click", () => {
    state.benchmarkReviewDecisions = new Map();
    resetAllBenchmarkTargetBounds();
    state.benchmarkTargetEdits = new Map();
    state.benchmarkDeletedTargets = new Set();
    state.benchmarkFilters = resetBenchmarkFilters();
    refreshBenchmarkReviewUi();
  });

  toolbar.append(acceptShown, reviewShown, reset);
  return toolbar;
}

function setBenchmarkDecisionForTargets(targets, decision) {
  const normalizedDecision = normalizeBenchmarkReviewDecision(decision);
  if (!normalizedDecision) {
    return;
  }

  const now = new Date().toISOString();
  targets.forEach((target) => {
    state.benchmarkReviewDecisions.set(target.reviewKey, { decision: normalizedDecision, reviewedAt: now });
  });
  refreshBenchmarkReviewUi();
}

function benchmarkReviewQueueKindCounts(targets) {
  const counts = new Map();
  targets.forEach((target) => {
    const kind = benchmarkReviewQueueKind(target.reviewQueueKind);
    counts.set(kind, (counts.get(kind) ?? 0) + 1);
  });

  return [...counts.entries()].sort((first, second) =>
    benchmarkReviewQueueKindSort(first[0]) - benchmarkReviewQueueKindSort(second[0])
    || first[0].localeCompare(second[0]));
}

function benchmarkReviewQueueKindSort(kind) {
  switch (benchmarkReviewQueueKind(kind)) {
    case "PrecisionExtra":
      return 0;
    case "SpotCheckExtra":
      return 1;
    case "ReviewOnly":
      return 2;
    default:
      return 3;
  }
}

function renderBenchmarkFilterControls(targets, filteredTargets) {
  const filters = state.benchmarkFilters ?? resetBenchmarkFilters();
  const controls = document.createElement("div");
  controls.className = "benchmark-filters";

  const query = document.createElement("input");
  query.type = "search";
  query.value = filters.query || "";
  query.placeholder = "Search targets";
  query.setAttribute("aria-label", "Search benchmark targets");
  query.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      state.benchmarkFilters.query = query.value;
      refreshBenchmarkReviewUi();
    }
  });

  const status = benchmarkFilterSelect("Status", filters.status, [
    ["all", "All status"],
    ["unreviewed", "Unreviewed"],
    ["accepted", "Accepted"],
    ["rejected", "Rejected"],
    ["needsReview", "Needs review"]
  ], (value) => {
    state.benchmarkFilters.status = value;
    refreshBenchmarkReviewUi();
  });

  const detectorOptions = [["all", "All detectors"]]
    .concat(benchmarkDetectorCounts(targets).map(([label]) => {
      const descriptor = benchmarkMetricDescriptors.find((item) => item.label === label);
      return [descriptor?.key || label, label];
    }));
  const detector = benchmarkFilterSelect("Detector", filters.detector, detectorOptions, (value) => {
    state.benchmarkFilters.detector = value;
    refreshBenchmarkReviewUi();
  });

  const queueKind = state.benchmarkResult
    ? benchmarkFilterSelect("Queue kind", filters.queueKind, [
      ["all", "All queue"],
      ["PrecisionExtra", "Precision extra"],
      ["SpotCheckExtra", "Spot-check extra"],
      ["ReviewOnly", "Review-only"]
    ], (value) => {
      state.benchmarkFilters.queueKind = value;
      refreshBenchmarkReviewUi();
    })
    : null;

  const issue = benchmarkFilterSelect("Issue", filters.issue, [
    ["all", "All issues"],
    ["missingBounds", "No bounds"],
    ["lowConfidence", "Low confidence"],
    ["missingEvidence", "No evidence"],
    ["boundsEdited", "Bounds edited"],
    ["added", "Added"]
  ], (value) => {
    state.benchmarkFilters.issue = value;
    refreshBenchmarkReviewUi();
  });

  const page = benchmarkFilterSelect("Page", filters.page, [
    ["all", "All pages"],
    ["current", `Page ${state.currentPage}`],
    ["unpaged", "Unpaged"]
  ], (value) => {
    state.benchmarkFilters.page = value;
    refreshBenchmarkReviewUi();
  });

  const apply = document.createElement("button");
  apply.type = "button";
  apply.textContent = "Apply";
  apply.addEventListener("click", () => {
    state.benchmarkFilters.query = query.value;
    refreshBenchmarkReviewUi();
  });

  const clear = document.createElement("button");
  clear.type = "button";
  clear.textContent = "Clear";
  clear.disabled = !benchmarkFiltersActive();
  clear.addEventListener("click", () => {
    state.benchmarkFilters = resetBenchmarkFilters();
    refreshBenchmarkReviewUi();
  });

  const count = document.createElement("small");
  count.textContent = `${filteredTargets.length} / ${targets.length}`;

  controls.append(query, status, detector);
  if (queueKind) {
    controls.appendChild(queueKind);
  }
  controls.append(issue, page, apply, clear, count);
  return controls;
}

function renderBenchmarkManualTargetPanel() {
  state.benchmarkManualTargetDraft ??= resetBenchmarkManualTargetDraft();
  const draft = state.benchmarkManualTargetDraft;
  const panel = document.createElement("div");
  panel.className = "benchmark-manual-target";

  const heading = document.createElement("strong");
  heading.textContent = "Manual target";

  const grid = document.createElement("div");
  grid.className = "benchmark-manual-grid";

  const detector = benchmarkManualSelect(
    "Detector",
    draft.detectorKey,
    manualBenchmarkDetectorKeys.map((key) => [key, benchmarkDetectorLabel(key)]),
    (value) => {
      state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft({
        detectorKey: value,
        pageNumber: draft.pageNumber || state.currentPage,
        bounds: draft.bounds
      });
      refreshBenchmarkReviewUi();
    });

  const id = benchmarkManualInput("ID", draft.id, (value) => {
    draft.id = value;
  });
  const label = benchmarkManualInput("Label", draft.label, (value) => {
    draft.label = value;
  });
  const text = benchmarkManualInput("Text", draft.text, (value) => {
    draft.text = value;
  });
  const marker = benchmarkManualInput("Marker", draft.marker, (value) => {
    draft.marker = value;
  });

  grid.append(detector, id, label, text, marker);

  const fieldOptions = manualBenchmarkFieldOptions[draft.detectorKey];
  if (fieldOptions?.kindOptions) {
    grid.appendChild(benchmarkManualSelect(
      fieldOptions.kindLabel,
      draft.kind,
      fieldOptions.kindOptions.map((value) => [value, value || "-"]),
      (value) => {
        draft.kind = value;
      }));
  }

  if (fieldOptions?.subtypeOptions) {
    grid.appendChild(benchmarkManualSelect(
      fieldOptions.subtypeLabel,
      draft.subtype,
      fieldOptions.subtypeOptions.map((value) => [value, value || "-"]),
      (value) => {
        draft.subtype = value;
      }));
  }

  (fieldOptions?.extraFields ?? []).forEach((field) => {
    grid.appendChild(benchmarkManualSelect(
      field.label,
      draft[field.key] ?? "",
      field.options.map((value) => [value, value || "-"]),
      (value) => {
        draft[field.key] = value;
      }));
  });

  if (["objectGroupMetrics", "objectAggregateMetrics", "routingObstacleMetrics"].includes(draft.detectorKey)) {
    grid.appendChild(benchmarkManualInput("Min", draft.minCount, (value) => {
      draft.minCount = value;
    }, "number"));
  }

  if (["objectGroupMetrics", "objectAggregateMetrics"].includes(draft.detectorKey)) {
    grid.appendChild(benchmarkManualSelect("Review", draft.requiresReview, [
      ["", "-"],
      ["true", "Yes"],
      ["false", "No"]
    ], (value) => {
      draft.requiresReview = value;
    }));
  }

  if (["objectAggregateMetrics", "routingObstacleMetrics"].includes(draft.detectorKey)) {
    grid.appendChild(benchmarkManualSelect("Suppress", draft.suppressesChildObjects, [
      ["", "-"],
      ["true", "Yes"],
      ["false", "No"]
    ], (value) => {
      draft.suppressesChildObjects = value;
    }));
  }

  const bounds = draft.bounds ?? { x: "", y: "", width: "", height: "" };
  const pageValue = draft.pageNumber || state.currentPage || "";
  const pageInput = benchmarkManualInput("Page", pageValue, (value) => {
    draft.pageNumber = parsePositiveNumber(value, true) ?? "";
  }, "number");
  const xInput = benchmarkManualInput("X", bounds.x ?? "", (value) => updateManualDraftBound("x", value), "number");
  const yInput = benchmarkManualInput("Y", bounds.y ?? "", (value) => updateManualDraftBound("y", value), "number");
  const widthInput = benchmarkManualInput("W", bounds.width ?? "", (value) => updateManualDraftBound("width", value), "number");
  const heightInput = benchmarkManualInput("H", bounds.height ?? "", (value) => updateManualDraftBound("height", value), "number");
  grid.append(pageInput, xInput, yInput, widthInput, heightInput);

  const actions = document.createElement("div");
  actions.className = "benchmark-manual-actions";

  const draw = document.createElement("button");
  draw.type = "button";
  draw.textContent = draft.drawing ? "Drawing" : "Draw box";
  draw.className = draft.drawing ? "active" : "";
  draw.addEventListener("click", () => {
    draft.drawing = !draft.drawing;
    state.benchmarkDrawBox = null;
    setStatus(draft.drawing ? "Drawing manual target" : "Ready");
    refreshBenchmarkReviewUi();
  });

  const useSelection = document.createElement("button");
  useSelection.type = "button";
  useSelection.textContent = "Use selection";
  useSelection.disabled = !state.selectedItem?.bounds;
  useSelection.addEventListener("click", () => seedManualBenchmarkTargetFromSelection());

  const add = document.createElement("button");
  add.type = "button";
  add.textContent = "Add target";
  add.addEventListener("click", () => addManualBenchmarkTarget());

  const clear = document.createElement("button");
  clear.type = "button";
  clear.textContent = "Clear draft";
  clear.addEventListener("click", () => {
    state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft({
      detectorKey: draft.detectorKey,
      pageNumber: state.currentPage
    });
    state.benchmarkDrawBox = null;
    refreshBenchmarkReviewUi();
  });

  actions.append(draw, useSelection, add, clear);
  panel.append(heading, grid, actions);
  return panel;
}

function benchmarkManualInput(label, value, onInput, type = "text") {
  const wrapper = document.createElement("label");
  const caption = document.createElement("span");
  const input = document.createElement("input");
  caption.textContent = label;
  input.type = type;
  if (type === "number") {
    input.step = "0.1";
  }
  input.value = value == null ? "" : String(value);
  input.addEventListener("input", () => onInput(input.value));
  input.addEventListener("change", () => {
    if (type === "number") {
      drawOverlay();
    }
  });
  wrapper.append(caption, input);
  return wrapper;
}

function benchmarkManualSelect(label, value, options, onChange) {
  const wrapper = document.createElement("label");
  const caption = document.createElement("span");
  const select = document.createElement("select");
  caption.textContent = label;
  options.forEach(([optionValue, optionLabel]) => {
    const option = document.createElement("option");
    option.value = optionValue;
    option.textContent = optionLabel;
    option.selected = optionValue === value;
    select.appendChild(option);
  });
  select.addEventListener("change", () => onChange(select.value));
  wrapper.append(caption, select);
  return wrapper;
}

function updateManualDraftBound(key, value) {
  const number = key === "width" || key === "height"
    ? parsePositiveNumber(value)
    : parseFiniteNumber(value);
  const current = state.benchmarkManualTargetDraft.bounds ?? { x: 0, y: 0, width: 0, height: 0 };
  if (number == null) {
    return;
  }

  state.benchmarkManualTargetDraft.bounds = normalizeRect({
    ...current,
    [key]: number
  });
  state.benchmarkManualTargetDraft.pageNumber = state.benchmarkManualTargetDraft.pageNumber || state.currentPage;
}

function seedManualBenchmarkTargetFromSelection() {
  if (!state.selectedItem?.bounds) {
    setStatus("Select a bounded detection first");
    return;
  }

  const draftTarget = state.selectedItem.benchmarkDraft?.target;
  const detectorKey = state.selectedItem.benchmarkDraft?.detectorKey || state.benchmarkManualTargetDraft.detectorKey;
  const fieldOptions = manualBenchmarkFieldOptions[detectorKey] ?? {};
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft({
    detectorKey,
    bounds: clonePlain(draftTarget?.bounds ?? state.selectedItem.bounds),
    pageNumber: normalizedPageNumber(draftTarget?.pageNumber ?? state.selectedItem.pageNumber) ?? state.currentPage,
    label: draftTarget?.label || state.selectedItem.roomLabel || state.selectedItem.label || "",
    text: draftTarget?.text || "",
    marker: draftTarget?.marker || "",
    minCount: draftTarget?.minCount ?? "",
    requiresReview: draftTarget?.requiresReview == null ? "" : String(Boolean(draftTarget.requiresReview)),
    kind: fieldOptions.kindKey ? draftTarget?.[fieldOptions.kindKey] || "" : "",
    subtype: fieldOptions.subtypeKey ? draftTarget?.[fieldOptions.subtypeKey] || "" : "",
    routingSourceKind: draftTarget?.routingSourceKind || "",
    routingObstacleKind: draftTarget?.routingObstacleKind || "",
    routingInfluence: draftTarget?.routingInfluence || "",
    structuralInfluence: draftTarget?.structuralInfluence || "",
    roomUseKind: draftTarget?.roomUseKind || "",
    objectCategory: draftTarget?.objectCategory || "",
    objectKind: draftTarget?.objectKind || "",
    openingOperation: draftTarget?.openingOperation || "",
    objectCandidateId: draftTarget?.objectCandidateId || "",
    suppressedByAggregateId: draftTarget?.suppressedByAggregateId || "",
    suppressionReason: draftTarget?.suppressionReason || "",
    suppressionAction: draftTarget?.suppressionAction || "",
    replacementRoutingObstacleId: draftTarget?.replacementRoutingObstacleId || "",
    roomUseHintId: draftTarget?.roomUseHintId || "",
    suppressesChildObjects: draftTarget?.suppressesChildObjects == null ? "" : String(Boolean(draftTarget.suppressesChildObjects))
  });
  setStatus("Manual target seeded");
  refreshBenchmarkReviewUi();
}

function addManualBenchmarkTarget() {
  if (!state.benchmarkManifest) {
    setStatus("Load a benchmark manifest first");
    return;
  }

  const fixtureIndex = 0;
  const fixture = state.benchmarkManifest.fixtures?.[fixtureIndex];
  if (!fixture) {
    setStatus("Load a benchmark fixture first");
    return;
  }

  const draft = state.benchmarkManualTargetDraft ?? resetBenchmarkManualTargetDraft();
  const descriptor = benchmarkMetricDescriptors.find((entry) => entry.key === draft.detectorKey);
  if (!descriptor || !manualBenchmarkDetectorKeys.includes(draft.detectorKey)) {
    setStatus("Unsupported manual target type");
    return;
  }

  const bounds = normalizeRect(draft.bounds);
  const pageNumber = normalizedPageNumber(draft.pageNumber) ?? state.currentPage;
  if (!bounds || bounds.width < 1 || bounds.height < 1) {
    setStatus("Draw or enter target bounds first");
    return;
  }

  const sequence = state.benchmarkAddedTargetSequence++;
  const id = cleanManualTargetId(draft.id) || uniqueBenchmarkTargetId(draft.detectorKey, "manual-target");
  const now = new Date().toISOString();
  const target = buildManualBenchmarkTarget(draft, id, bounds, pageNumber);
  const reviewKey = `manual|${fixture.id || "fixture-1"}|${draft.detectorKey}|${id}|${sequence}`;
  const normalized = normalizeBenchmarkTargetForState(target, {
    reviewKey,
    detectorKey: draft.detectorKey,
    detectorLabel: descriptor.label,
    targetIndex: Number.MAX_SAFE_INTEGER - sequence,
    fixtureIndex,
    fixtureId: fixture.id || "fixture-1",
    fixtureName: fixture.name || fixture.id || "fixture-1",
    sourcePath: fixture.sourcePath || "",
    isAdded: true,
    addedAt: now
  });

  state.benchmarkTargets.push(normalized);
  state.benchmarkReviewDecisions.set(reviewKey, { decision: "accepted", reviewedAt: now, createdAt: now });
  state.selectedItem = describeBenchmarkTarget(normalized);
  state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft({
    detectorKey: draft.detectorKey,
    pageNumber
  });
  state.benchmarkDrawBox = null;
  setStatus("Manual benchmark target added");
  refreshBenchmarkReviewUi(reviewKey);
}

function buildManualBenchmarkTarget(draft, id, bounds, pageNumber) {
  const fieldOptions = manualBenchmarkFieldOptions[draft.detectorKey] ?? {};
  const target = {
    id,
    pageNumber,
    bounds,
    label: cleanManualText(draft.label),
    text: cleanManualText(draft.text),
    marker: cleanManualText(draft.marker),
    confidence: 1,
    evidence: [
      "Manual reviewer box added in OpenPlanTrace Viewer.",
      "No source primitive IDs because this target records a missed or reviewer-defined detection."
    ]
  };

  if (fieldOptions.kindKey && cleanManualText(draft.kind)) {
    target[fieldOptions.kindKey] = cleanManualText(draft.kind);
  }

  if (fieldOptions.subtypeKey && cleanManualText(draft.subtype)) {
    target[fieldOptions.subtypeKey] = cleanManualText(draft.subtype);
  }

  (fieldOptions.extraFields ?? []).forEach((field) => {
    const value = cleanManualText(draft[field.key]);
    if (value) {
      target[field.key] = value;
    }
  });

  if (["objectGroupMetrics", "objectAggregateMetrics", "routingObstacleMetrics"].includes(draft.detectorKey)) {
    const minCount = parsePositiveNumber(draft.minCount, true);
    if (minCount != null) {
      target.minCount = minCount;
    }
  }

  if (["objectGroupMetrics", "objectAggregateMetrics"].includes(draft.detectorKey)) {
    if (draft.requiresReview === "true" || draft.requiresReview === "false") {
      target.requiresReview = draft.requiresReview === "true";
    }
  }

  if (["objectAggregateMetrics", "routingObstacleMetrics"].includes(draft.detectorKey)
      && (draft.suppressesChildObjects === "true" || draft.suppressesChildObjects === "false")) {
    target.suppressesChildObjects = draft.suppressesChildObjects === "true";
  }

  if (draft.detectorKey === "routingSuppressedObjectMetrics") {
    [
      "objectCandidateId",
      "suppressedByAggregateId",
      "replacementRoutingObstacleId",
      "roomUseHintId"
    ].forEach((key) => {
      const value = cleanManualText(draft[key]);
      if (value) {
        target[key] = value;
      }
    });
  }

  return cleanBenchmarkTarget(target);
}

function cleanManualTargetId(value) {
  return String(value || "").trim().replace(/[^a-z0-9_-]+/gi, "-").replace(/^-+|-+$/g, "");
}

function cleanManualText(value) {
  return String(value ?? "").trim();
}

function benchmarkFilterSelect(label, value, options, onChange) {
  const select = document.createElement("select");
  select.setAttribute("aria-label", label);
  options.forEach(([optionValue, optionLabel]) => {
    const option = document.createElement("option");
    option.value = optionValue;
    option.textContent = optionLabel;
    option.selected = optionValue === value;
    select.appendChild(option);
  });
  select.addEventListener("change", () => onChange(select.value));
  return select;
}

function benchmarkFiltersActive() {
  const filters = state.benchmarkFilters ?? resetBenchmarkFilters();
  return Object.entries(defaultBenchmarkFilters)
    .some(([key, value]) => filters[key] !== value);
}

function benchmarkDetectorCounts(targets) {
  const counts = new Map();
  targets.forEach((target) => {
    counts.set(target.detectorLabel, (counts.get(target.detectorLabel) ?? 0) + 1);
  });

  return [...counts.entries()].sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]));
}

function benchmarkReviewSummary(targets) {
  const summary = {
    accepted: 0,
    rejected: 0,
    needsReview: 0,
    unreviewed: 0,
    reviewed: 0
  };

  targets.forEach((target) => {
    const decision = benchmarkTargetDecision(target);
    if (decision === "accepted") {
      summary.accepted++;
      summary.reviewed++;
    } else if (decision === "rejected") {
      summary.rejected++;
      summary.reviewed++;
    } else if (decision === "needsReview") {
      summary.needsReview++;
      summary.reviewed++;
    } else {
      summary.unreviewed++;
    }
  });

  return summary;
}

function renderBenchmarkReviewToolbar(targets, filteredTargets = targets) {
  const toolbar = document.createElement("div");
  toolbar.className = "benchmark-review-toolbar";

  const acceptAll = document.createElement("button");
  acceptAll.type = "button";
  acceptAll.textContent = "Accept shown";
  acceptAll.disabled = !filteredTargets.some((target) => !benchmarkTargetDecision(target));
  acceptAll.addEventListener("click", () => {
    const now = new Date().toISOString();
    filteredTargets.forEach((target) => {
      if (!benchmarkTargetDecision(target)) {
        state.benchmarkReviewDecisions.set(target.reviewKey, { decision: "accepted", reviewedAt: now });
      }
    });
    refreshBenchmarkReviewUi();
  });

  const reset = document.createElement("button");
  reset.type = "button";
  reset.textContent = "Reset all";
  reset.disabled = state.benchmarkReviewDecisions.size === 0
    && state.benchmarkTargetEdits.size === 0
    && state.benchmarkDeletedTargets.size === 0
    && !state.benchmarkTargets.some((target) => target.isAdded)
    && !benchmarkFiltersActive();
  reset.addEventListener("click", () => {
    state.benchmarkReviewDecisions = new Map();
    resetAllBenchmarkTargetBounds();
    state.benchmarkTargetEdits = new Map();
    state.benchmarkDeletedTargets = new Set();
    state.benchmarkTargets = state.benchmarkTargets.filter((target) => !target.isAdded);
    state.benchmarkAddedTargetSequence = 1;
    state.benchmarkFilters = resetBenchmarkFilters();
    state.benchmarkManualTargetDraft = resetBenchmarkManualTargetDraft();
    state.benchmarkDrawBox = null;
    refreshBenchmarkReviewUi();
  });

  const exportButton = document.createElement("button");
  exportButton.type = "button";
  exportButton.textContent = "Export reviewed";
  exportButton.addEventListener("click", () => downloadReviewedBenchmarkManifest());

  const sessionButton = document.createElement("button");
  sessionButton.type = "button";
  sessionButton.textContent = "Session JSON";
  sessionButton.addEventListener("click", () => downloadBenchmarkReviewSession());

  const reportButton = document.createElement("button");
  reportButton.type = "button";
  reportButton.textContent = "Report MD";
  reportButton.addEventListener("click", () => downloadBenchmarkReviewReport());

  toolbar.append(acceptAll, reset, exportButton, sessionButton, reportButton);
  return toolbar;
}

function compareBenchmarkTargets(first, second) {
  return (first.pageNumber ?? Number.MAX_SAFE_INTEGER) - (second.pageNumber ?? Number.MAX_SAFE_INTEGER)
    || first.fixtureId.localeCompare(second.fixtureId)
    || first.detectorLabel.localeCompare(second.detectorLabel)
    || String(first.id).localeCompare(String(second.id));
}

function renderBenchmarkTargetButton(target) {
  const item = document.createElement("button");
  item.type = "button";
  item.className = [
    "benchmark-target-row",
    target.bounds ? "" : "missing-bounds",
    isLowConfidenceBenchmarkTarget(target) ? "low" : "",
    benchmarkTargetHasEvidence(target) ? "" : "missing-evidence",
    benchmarkTargetBoundsEdited(target) ? "bounds-edited" : "",
    target.isAdded ? "added" : "",
    target.isReviewQueueItem ? benchmarkReviewQueueKindClass(target.reviewQueueKind) : "",
    benchmarkTargetDecision(target)
  ].filter(Boolean).join(" ");
  item.title = `Select ${target.detectorLabel} target ${target.id}`;

  const label = document.createElement("strong");
  label.textContent = `${target.detectorLabel}: ${target.id}`;

  const detail = document.createElement("span");
  detail.textContent = [
    target.pageNumber == null ? "unpaged" : `page ${target.pageNumber}`,
    target.confidence == null ? "" : `${Math.round(Number(target.confidence) * 100)}%`,
    target.isReviewQueueItem ? benchmarkReviewQueueKindLabel(target.reviewQueueKind) : "",
    target.isAdded ? "added" : "",
    benchmarkTargetDecisionLabel(benchmarkTargetDecision(target)),
    benchmarkTargetBoundsEdited(target) ? "bounds edited" : "",
    benchmarkTargetCriteria(target)
  ].filter(Boolean).join(" - ");

  const meta = document.createElement("small");
  meta.textContent = [
    target.fixtureName || target.fixtureId,
    target.recommendedAction || "",
    target.sourceLayers?.length ? `layers ${target.sourceLayers.join(", ")}` : "",
    target.bounds ? "" : "missing bounds"
  ].filter(Boolean).join(" - ");

  item.append(label, detail);
  if (meta.textContent) {
    item.appendChild(meta);
  }

  item.addEventListener("click", async () => {
    const selected = describeBenchmarkTarget(target);
    state.selectedItem = selected;

    if (target.pageNumber && target.pageNumber !== state.currentPage && pageExists(target.pageNumber)) {
      state.currentPage = target.pageNumber;
      await renderCurrentPage();
    }

    setSelection(selected);
    setBenchmarkDetails();
  });

  return item;
}

function describeBenchmarkTarget(target) {
  const decision = benchmarkTargetDecision(target);
  return {
    type: "benchmark target",
    id: target.id,
    kind: target.detectorLabel,
    confidence: target.confidence,
    reviewKey: target.reviewKey,
    reviewDecision: decision,
    reviewQueueKind: target.reviewQueueKind,
    recommendedAction: target.recommendedAction,
    bounds: target.bounds,
    boundsEdited: benchmarkTargetBoundsEdited(target),
    targetAdded: Boolean(target.isAdded),
    sourceLayers: target.sourceLayers || [],
    sourcePrimitiveIds: target.sourcePrimitiveIds || [],
    pageNumber: target.pageNumber,
    measurement: target.bounds
      ? `${formatNumber(target.bounds.width)} x ${formatNumber(target.bounds.height)} drawing units`
      : "",
    scaleGroupId: "",
    evidence: target.evidence || [],
    hostWallIds: [],
    connectedRoomLinks: [],
    roomId: "",
    roomLabel: "",
    nearbyText: [],
    swing: "",
    metadata: [
      `review ${benchmarkTargetDecisionLabel(decision)}`,
      target.isAdded ? "added target" : "",
      benchmarkTargetBoundsEdited(target) ? "bounds edited" : "",
      target.isReviewQueueItem ? benchmarkReviewQueueKindLabel(target.reviewQueueKind) : "",
      target.recommendedAction || "",
      `fixture ${target.fixtureName || target.fixtureId}`,
      benchmarkTargetCriteria(target),
      target.minRecall == null ? "" : `min recall ${formatNumber(target.minRecall)}`,
      target.minPrecision == null ? "" : `min precision ${formatNumber(target.minPrecision)}`,
      target.sourcePath ? `source ${target.sourcePath}` : ""
    ].filter(Boolean).join(" | ")
  };
}

function benchmarkTargetCriteria(target) {
  const pairs = [
    ["region", target.regionKind],
    ["dimension", target.dimensionKind],
    ["orientation", target.dimensionOrientation || target.gridAxisOrientation],
    ["annotation", target.annotationKind],
    ["opening", target.openingType],
    ["operation", target.openingOperation],
    ["object", target.objectCategory],
    ["object kind", target.objectKind],
    ["layer", target.layerCategory],
    ["routing source", target.routingSourceKind],
    ["routing obstacle", target.routingObstacleKind],
    ["routing influence", target.routingInfluence],
    ["structural influence", target.structuralInfluence],
    ["room use", target.roomUseKind],
    ["child object", target.objectCandidateId],
    ["aggregate", target.suppressedByAggregateId],
    ["suppression reason", target.suppressionReason],
    ["suppression action", target.suppressionAction],
    ["replacement", target.replacementRoutingObstacleId],
    ["room-use hint", target.roomUseHintId],
    ["detected tags", target.detectedTags?.join(", ")],
    ["label", target.label],
    ["text", target.text],
    ["marker", target.marker],
    ["min count", target.minCount],
    ["review", target.requiresReview == null ? null : (target.requiresReview ? "yes" : "no")],
    ["suppresses child objects", target.suppressesChildObjects == null ? null : (target.suppressesChildObjects ? "yes" : "no")],
    ["iou", target.minIntersectionOverUnion],
    ["center", target.maxCenterDistance]
  ];

  return pairs
    .filter(([, value]) => value !== null && value !== undefined && value !== "")
    .map(([label, value]) => `${label} ${value}`)
    .join(", ");
}

function cleanBenchmarkTarget(target) {
  const cleaned = {};
  [
    "id",
    "pageNumber",
    "bounds",
    "minIntersectionOverUnion",
    "maxCenterDistance",
    "label",
    "text",
    "marker",
    "minCount",
    "requiresReview",
    "regionKind",
    "dimensionKind",
    "dimensionOrientation",
    "annotationKind",
    "gridAxisOrientation",
    "openingType",
    "openingOperation",
    "objectCategory",
    "objectKind",
    "layerCategory",
    "routingSourceKind",
    "routingObstacleKind",
    "routingInfluence",
    "structuralInfluence",
    "roomUseKind",
    "suppressesChildObjects",
    "objectCandidateId",
    "suppressedByAggregateId",
    "suppressionReason",
    "suppressionAction",
    "replacementRoutingObstacleId",
    "roomUseHintId",
    "detectedTags",
    "confidence",
    "sourcePrimitiveIds",
    "sourceLayers",
    "evidence",
    "xReview"
  ].forEach((key) => {
    const value = target[key];
    if (value === null || value === undefined || value === "") {
      return;
    }

    if (Array.isArray(value) && value.length === 0) {
      return;
    }

    cleaned[key] = value;
  });

  return cleaned;
}

function downloadReviewedBenchmarkManifest() {
  if (!state.benchmarkManifest) {
    return;
  }

  const manifest = buildReviewedBenchmarkManifest();
  downloadBlob(
    new Blob([JSON.stringify(manifest, null, 2)], { type: "application/json" }),
    `${safeBenchmarkName()}-reviewed-benchmark.json`);
}

function downloadBenchmarkReviewSession() {
  if (!state.benchmarkManifest) {
    return;
  }

  const session = buildBenchmarkReviewSession();
  downloadBlob(
    new Blob([JSON.stringify(session, null, 2)], { type: "application/json" }),
    `${safeBenchmarkName()}-benchmark-review-session.json`);
}

function downloadBenchmarkReviewReport() {
  if (!state.benchmarkManifest) {
    return;
  }

  const markdown = buildBenchmarkReviewMarkdown();
  downloadBlob(
    new Blob([markdown], { type: "text/markdown" }),
    `${safeBenchmarkName()}-benchmark-review-report.md`);
}

function buildBenchmarkReviewSession() {
  const exportedAt = new Date().toISOString();
  const targets = activeBenchmarkTargets();
  const filteredTargets = filteredBenchmarkTargets(targets);
  const summary = benchmarkReviewSummary(targets);
  const deletedTargets = [...state.benchmarkDeletedTargets]
    .map((reviewKey) => state.benchmarkTargets.find((target) => target.reviewKey === reviewKey))
    .filter(Boolean);
  const addedTargets = targets.filter((target) => target.isAdded);
  const boundsEdits = [...state.benchmarkTargetEdits.entries()]
    .map(([reviewKey, edit]) => {
      const target = state.benchmarkTargets.find((item) => item.reviewKey === reviewKey);
      return {
        reviewKey,
        target: target ? benchmarkReviewSessionTarget(target) : null,
        pageNumber: edit.pageNumber ?? null,
        bounds: clonePlain(edit.bounds),
        editedAt: edit.editedAt
      };
    });
  const decisions = targets
    .filter((target) => benchmarkTargetDecision(target))
    .map((target) => benchmarkReviewSessionTarget(target));
  const reviewIssues = targets
    .map((target) => ({
      ...benchmarkReviewSessionTarget(target),
      flags: benchmarkTargetReviewFlags(target)
    }))
    .filter((target) => target.flags.length > 0);

  return {
    schemaVersion: "openplantrace.viewer-benchmark-review-session.v1",
    tool: "OpenPlanTrace Viewer",
    exportedAt,
    manifest: {
      schemaVersion: state.benchmarkManifest.schemaVersion,
      name: state.benchmarkManifest.name,
      label: state.benchmarkManifest.label,
      fixtureCount: state.benchmarkManifest.fixtures?.length ?? 0,
      targetCount: targets.length
    },
    scan: {
      documentId: state.scan?.documentId || "",
      pageCount: state.scan?.pages?.length ?? 0,
      qualityGrade: state.scan?.quality?.grade || "",
      qualityConfidence: state.scan?.quality?.overallConfidence ?? null,
      diagnostics: {
        infoCount: state.scan?.diagnostics?.infoCount ?? 0,
        warningCount: state.scan?.diagnostics?.warningCount ?? 0,
        errorCount: state.scan?.diagnostics?.errorCount ?? 0,
        stageCount: state.scan?.diagnostics?.stages?.length ?? 0,
        durationMilliseconds: state.scan?.diagnostics?.durationMilliseconds ?? null
      }
    },
    summary: {
      activeTargetCount: targets.length,
      filteredTargetCount: filteredTargets.length,
      acceptedCount: summary.accepted,
      rejectedCount: summary.rejected,
      needsReviewCount: summary.needsReview,
      unreviewedCount: summary.unreviewed,
      addedTargetCount: addedTargets.length,
      deletedTargetCount: deletedTargets.length,
      boundsEditedCount: targets.filter(benchmarkTargetBoundsEdited).length,
      missingBoundsCount: targets.filter((target) => !target.bounds).length,
      lowConfidenceCount: targets.filter(isLowConfidenceBenchmarkTarget).length,
      missingEvidenceCount: targets.filter((target) => !benchmarkTargetHasEvidence(target)).length,
      filters: { ...state.benchmarkFilters }
    },
    decisions,
    boundsEdits,
    addedTargets: addedTargets.map((target) => ({
      ...benchmarkReviewSessionTarget(target),
      manifestTarget: exportAddedBenchmarkTarget(target, exportedAt)
    })),
    deletedTargets: deletedTargets.map((target) => benchmarkReviewSessionTarget(target)),
    reviewIssues
  };
}

function benchmarkReviewSessionTarget(target) {
  return {
    reviewKey: target.reviewKey,
    id: target.id,
    detectorKey: target.detectorKey,
    detectorLabel: target.detectorLabel,
    fixtureId: target.fixtureId,
    fixtureName: target.fixtureName,
    pageNumber: target.pageNumber ?? null,
    bounds: clonePlain(target.bounds),
    originalPageNumber: target.originalPageNumber ?? null,
    originalBounds: clonePlain(target.originalBounds),
    confidence: target.confidence ?? null,
    decision: benchmarkTargetDecision(target) || "unreviewed",
    isAdded: Boolean(target.isAdded),
    boundsEdited: benchmarkTargetBoundsEdited(target),
    hasEvidence: benchmarkTargetHasEvidence(target),
    sourceLayers: target.sourceLayers || [],
    sourcePrimitiveIds: target.sourcePrimitiveIds || [],
    detectedTags: target.detectedTags || [],
    evidence: target.evidence || [],
    criteria: benchmarkTargetCriteria(target)
  };
}

function benchmarkTargetReviewFlags(target) {
  const decision = benchmarkTargetDecision(target);
  const flags = [];

  if (!target.bounds) {
    flags.push("missing_bounds");
  }

  if (isLowConfidenceBenchmarkTarget(target)) {
    flags.push("low_confidence");
  }

  if (!benchmarkTargetHasEvidence(target)) {
    flags.push("missing_evidence");
  }

  if (!decision) {
    flags.push("unreviewed");
  } else if (decision === "rejected") {
    flags.push("rejected");
  } else if (decision === "needsReview") {
    flags.push("needs_review");
  }

  if (target.isAdded) {
    flags.push("added");
  }

  if (benchmarkTargetBoundsEdited(target)) {
    flags.push("bounds_edited");
  }

  return flags;
}

function buildBenchmarkReviewMarkdown() {
  const session = buildBenchmarkReviewSession();
  const lines = [
    `# ${markdownText(session.manifest.name || "OpenPlanTrace")} Benchmark Review`,
    "",
    `- Exported: ${session.exportedAt}`,
    `- Manifest: ${markdownText(session.manifest.label || session.manifest.name || "-")}`,
    `- Fixtures: ${session.manifest.fixtureCount}`,
    `- Targets: ${session.summary.activeTargetCount}`,
    `- Shown by current filters: ${session.summary.filteredTargetCount}`,
    `- Accepted: ${session.summary.acceptedCount}`,
    `- Rejected: ${session.summary.rejectedCount}`,
    `- Needs review: ${session.summary.needsReviewCount}`,
    `- Unreviewed: ${session.summary.unreviewedCount}`,
    `- Added: ${session.summary.addedTargetCount}`,
    `- Deleted: ${session.summary.deletedTargetCount}`,
    `- Bounds edited: ${session.summary.boundsEditedCount}`,
    `- Missing bounds: ${session.summary.missingBoundsCount}`,
    `- Low confidence: ${session.summary.lowConfidenceCount}`,
    `- Missing evidence: ${session.summary.missingEvidenceCount}`,
    "",
    "## Active Filters",
    "",
    `- Query: ${markdownText(session.summary.filters.query || "-")}`,
    `- Detector: ${markdownText(session.summary.filters.detector)}`,
    `- Status: ${markdownText(session.summary.filters.status)}`,
    `- Issue: ${markdownText(session.summary.filters.issue)}`,
    `- Page: ${markdownText(session.summary.filters.page)}`,
    "",
    "## Review Issues",
    ""
  ];

  if (!session.reviewIssues.length) {
    lines.push("No review issues found.");
  } else {
    session.reviewIssues.slice(0, 120).forEach((target) => {
      lines.push(`- ${markdownText(target.detectorLabel)} ${markdownText(target.id)}: ${target.flags.join(", ")}${target.pageNumber == null ? "" : `, page ${target.pageNumber}`}${target.confidence == null ? "" : `, confidence ${formatNumber(target.confidence)}`}`);
      if (target.criteria) {
        lines.push(`  Criteria: ${markdownText(target.criteria)}`);
      }
    });

    if (session.reviewIssues.length > 120) {
      lines.push(`- ${session.reviewIssues.length - 120} more issue targets are included in the session JSON.`);
    }
  }

  if (session.deletedTargets.length) {
    lines.push("", "## Deleted Targets", "");
    session.deletedTargets.forEach((target) => {
      lines.push(`- ${markdownText(target.detectorLabel)} ${markdownText(target.id)} (${markdownText(target.reviewKey)})`);
    });
  }

  return `${lines.join("\n")}\n`;
}

function markdownText(value) {
  return String(value ?? "-").replace(/\s+/g, " ").trim();
}

function buildReviewedBenchmarkManifest() {
  const reviewedAt = new Date().toISOString();
  const manifest = clonePlain(state.benchmarkManifest.raw ?? {
    schemaVersion: state.benchmarkManifest.schemaVersion,
    name: state.benchmarkManifest.name,
    fixtures: state.benchmarkManifest.fixtures
  });
  const targets = activeBenchmarkTargets();
  const summary = benchmarkReviewSummary(targets);

  manifest.name = manifest.name || state.benchmarkManifest.name || "Reviewed benchmark manifest";
  manifest.xReview = {
    tool: "OpenPlanTrace Viewer",
    reviewedAt,
    targetCount: targets.length,
    acceptedCount: summary.accepted,
    rejectedCount: summary.rejected,
    needsReviewCount: summary.needsReview,
    unreviewedCount: summary.unreviewed,
    addedTargetCount: targets.filter((target) => target.isAdded).length,
    deletedTargetCount: state.benchmarkDeletedTargets.size,
    boundsEditedCount: targets.filter(benchmarkTargetBoundsEdited).length
  };

  (manifest.fixtures ?? []).forEach((fixture, fixtureIndex) => {
    const fixtureId = fixture.id || `fixture-${fixtureIndex + 1}`;
    fixture.properties = {
      ...(fixture.properties ?? {}),
      benchmarkReviewStatus: summary.rejected || summary.accepted || summary.needsReview ? "reviewed-draft" : "unreviewed-draft",
      benchmarkReviewedAt: reviewedAt,
      benchmarkRejectedTargetCount: String(summary.rejected),
      benchmarkAddedTargetCount: String(targets.filter((target) => target.isAdded && target.fixtureIndex === fixtureIndex).length),
      benchmarkDeletedTargetCount: String(state.benchmarkDeletedTargets.size),
      benchmarkBoundsEditedTargetCount: String(targets.filter((target) => benchmarkTargetBoundsEdited(target) && target.fixtureIndex === fixtureIndex).length)
    };

    const expectations = fixture.expectations ?? {};
    benchmarkMetricDescriptors.forEach((descriptor) => {
      const metric = expectations[descriptor.key];
      if (!metric || !Array.isArray(metric.targets)) {
        return;
      }

      metric.targets = metric.targets
        .map((target, targetIndex) => {
          const key = benchmarkReviewKey(fixtureId, fixtureIndex, descriptor.key, target.id, targetIndex);
          const review = state.benchmarkReviewDecisions.get(key);
          const edit = state.benchmarkTargetEdits.get(key);
          if (state.benchmarkDeletedTargets.has(key) || review?.decision === "rejected") {
            return null;
          }

          const clone = clonePlain(target);
          delete clone.xReview;
          delete clone.review;

          if (edit) {
            if (edit.pageNumber == null) {
              delete clone.pageNumber;
            } else {
              clone.pageNumber = edit.pageNumber;
            }

            if (edit.bounds) {
              clone.bounds = clonePlain(edit.bounds);
            } else {
              delete clone.bounds;
            }
          }

          if (review?.decision || edit) {
            clone.xReview = {
              ...(review?.decision ? { decision: review.decision, reviewedAt: review.reviewedAt || reviewedAt } : {}),
              ...(edit ? { boundsEdited: true, boundsEditedAt: edit.editedAt || reviewedAt } : {}),
              tool: "OpenPlanTrace Viewer"
            };
          }

          return clone;
        })
        .filter(Boolean);

      if (metric.targets.length === 0) {
        delete metric.minRecall;
        delete metric.minPrecision;
      }
    });

    targets
      .filter((target) => target.isAdded && target.fixtureIndex === fixtureIndex)
      .forEach((target) => {
        if (benchmarkTargetDecision(target) === "rejected") {
          return;
        }

        const metric = expectations[target.detectorKey] ?? {};
        metric.targets = Array.isArray(metric.targets) ? metric.targets : [];
        metric.targets.push(exportAddedBenchmarkTarget(target, reviewedAt));
        expectations[target.detectorKey] = metric;
      });
  });

  return manifest;
}

function exportAddedBenchmarkTarget(target, reviewedAt) {
  const review = state.benchmarkReviewDecisions.get(target.reviewKey);
  const edit = state.benchmarkTargetEdits.get(target.reviewKey);
  const exported = cleanBenchmarkTarget({
    ...benchmarkTargetExportShape(target),
    ...(edit?.pageNumber == null ? {} : { pageNumber: edit.pageNumber }),
    ...(edit?.bounds ? { bounds: clonePlain(edit.bounds) } : {}),
    xReview: {
      targetAdded: true,
      createdAt: target.addedAt || reviewedAt,
      ...(review?.decision ? { decision: review.decision, reviewedAt: review.reviewedAt || reviewedAt } : {}),
      ...(edit ? { boundsEdited: true, boundsEditedAt: edit.editedAt || reviewedAt } : {}),
      tool: "OpenPlanTrace Viewer"
    }
  });

  return exported;
}

function benchmarkTargetExportShape(target) {
  return cleanBenchmarkTarget({
    id: target.id,
    pageNumber: target.pageNumber,
    bounds: target.bounds,
    minIntersectionOverUnion: target.minIntersectionOverUnion,
    maxCenterDistance: target.maxCenterDistance,
    label: target.label,
    text: target.text,
    marker: target.marker,
    minCount: target.minCount,
    requiresReview: target.requiresReview,
    regionKind: target.regionKind,
    dimensionKind: target.dimensionKind,
    dimensionOrientation: target.dimensionOrientation,
    annotationKind: target.annotationKind,
    gridAxisOrientation: target.gridAxisOrientation,
    openingType: target.openingType,
    openingOperation: target.openingOperation,
    objectCategory: target.objectCategory,
    objectKind: target.objectKind,
    layerCategory: target.layerCategory,
    routingSourceKind: target.routingSourceKind,
    routingObstacleKind: target.routingObstacleKind,
    routingInfluence: target.routingInfluence,
    structuralInfluence: target.structuralInfluence,
    roomUseKind: target.roomUseKind,
    suppressesChildObjects: target.suppressesChildObjects,
    objectCandidateId: target.objectCandidateId,
    suppressedByAggregateId: target.suppressedByAggregateId,
    suppressionReason: target.suppressionReason,
    suppressionAction: target.suppressionAction,
    replacementRoutingObstacleId: target.replacementRoutingObstacleId,
    roomUseHintId: target.roomUseHintId,
    detectedTags: target.detectedTags,
    confidence: target.confidence,
    sourcePrimitiveIds: target.sourcePrimitiveIds,
    sourceLayers: target.sourceLayers,
    evidence: target.evidence
  });
}

function clonePlain(value) {
  return JSON.parse(JSON.stringify(value ?? null));
}

function buildScanComparison(baseline, candidate, baselineLabel, candidateLabel) {
  const layers = {};
  const counts = comparableLayers.map((descriptor) => {
    const baselineItems = itemsForComparisonKey(baseline, descriptor.key);
    const candidateItems = itemsForComparisonKey(candidate, descriptor.key);
    const baselineIndex = indexComparableItems(baselineItems, descriptor.key);
    const candidateIndex = indexComparableItems(candidateItems, descriptor.key);
    const addedItems = [...candidateIndex.entries()]
      .filter(([key]) => !baselineIndex.has(key))
      .map(([, item]) => item);
    const removedItems = [...baselineIndex.entries()]
      .filter(([key]) => !candidateIndex.has(key))
      .map(([, item]) => item);

    const row = {
      key: descriptor.key,
      label: descriptor.label,
      baselineCount: baselineItems.length,
      candidateCount: candidateItems.length,
      delta: candidateItems.length - baselineItems.length,
      addedCount: addedItems.length,
      removedCount: removedItems.length,
      unchangedCount: Math.min(baselineItems.length, candidateItems.length) - Math.min(addedItems.length, removedItems.length),
      addedItems,
      removedItems
    };
    layers[descriptor.key] = row;
    return row;
  });

  const quality = compareQuality(baseline.quality, candidate.quality);
  const diagnostics = compareDiagnostics(baseline.diagnostics, candidate.diagnostics);
  const pageDelta = candidate.pages.length - baseline.pages.length;
  const geometryChangeCount = counts.reduce((sum, row) => sum + row.addedCount + row.removedCount, 0);
  const diagnosticChangeCount = diagnostics.addedCount + diagnostics.removedCount;
  const qualityIssueChangeCount = quality.addedIssueCodes.length + quality.removedIssueCodes.length;
  const qualityChangeCount = (quality.gradeChanged || Math.abs(quality.confidenceDelta) >= 0.01 ? 1 : 0) + qualityIssueChangeCount;
  const changeCount = geometryChangeCount + diagnosticChangeCount + Math.abs(pageDelta) + qualityChangeCount;

  return {
    baseline,
    candidate,
    baselineLabel,
    candidateLabel,
    baselinePageCount: baseline.pages.length,
    candidatePageCount: candidate.pages.length,
    pageDelta,
    counts,
    layers,
    quality,
    diagnostics,
    changeCount,
    hasChanges: changeCount > 0
  };
}

function itemsForComparisonKey(scan, key) {
  const value = scan?.[key];
  return Array.isArray(value) ? value : [];
}

function indexComparableItems(items, key) {
  const index = new Map();
  const occurrences = new Map();

  items.forEach((item, itemIndex) => {
    const baseKey = comparableItemKey(item, key, itemIndex);
    const occurrence = occurrences.get(baseKey) ?? 0;
    occurrences.set(baseKey, occurrence + 1);
    index.set(`${baseKey}#${occurrence}`, item);
  });

  return index;
}

function comparableItemKey(item, key, itemIndex = 0) {
  if (!item) {
    return `${key}:missing:${itemIndex}`;
  }

  if (key === "layers") {
    return `${key}:${layerKey(item.name)}:${item.likelyCategory || ""}`;
  }

  if (key === "diagnostics") {
    return `${key}:${item.severity || ""}:${item.stage || ""}:${item.code || ""}:${item.scope || ""}:${item.pageNumber || ""}:${item.message || ""}`;
  }

  if (key === "objectGroups" && item.signature) {
    return `${key}:signature:${item.signature}`;
  }

  if (key === "roomAdjacencyEdges") {
    const roomIds = [item.firstRoomId, item.secondRoomId].filter(Boolean).sort().join("|");
    return `${key}:${item.pageNumber ?? ""}:${roomIds}:${item.kind || ""}:${roundGeometryValue(item.sharedBoundaryLength)}`;
  }

  const page = item.pageNumber ?? item.number ?? "";
  const kind = item.category || item.kind || item.operation || item.type || item.detectionKind || item.label || item.orientation || "";
  const geometry = itemGeometrySignature(item);
  if (geometry) {
    return `${key}:${page}:${kind}:${geometry}`;
  }

  return `${key}:${page}:${kind}:${item.id || item.regionId || item.signature || itemIndex}`;
}

function itemGeometrySignature(item) {
  if (item.bounds) {
    return `rect:${rectSignature(item.bounds)}`;
  }

  if (item.representativeBounds) {
    return `rect:${rectSignature(item.representativeBounds)}`;
  }

  if (item.centerLine) {
    return `line:${lineSignature(item.centerLine)}`;
  }

  if (item.line) {
    return `line:${lineSignature(item.line)}`;
  }

  if (item.dimensionLine) {
    return `line:${lineSignature(item.dimensionLine)}`;
  }

  if (item.position) {
    return `point:${pointSignature(item.position)}`;
  }

  if (item.boundary?.length) {
    return `poly:${polygonSignature(item.boundary)}`;
  }

  return "";
}

function rectSignature(bounds) {
  if (!bounds) {
    return "";
  }

  return [
    bounds.x,
    bounds.y,
    bounds.width,
    bounds.height
  ].map(roundGeometryValue).join(",");
}

function lineSignature(line) {
  if (!line?.start || !line?.end) {
    return "";
  }

  const start = pointSignature(line.start);
  const end = pointSignature(line.end);
  return start < end ? `${start}-${end}` : `${end}-${start}`;
}

function pointSignature(point) {
  return `${roundGeometryValue(point?.x)},${roundGeometryValue(point?.y)}`;
}

function polygonSignature(points) {
  const bounds = boundsFromPoints(points);
  return `${points.length}:${rectSignature(bounds)}`;
}

function boundsFromPoints(points) {
  const xs = points.map((point) => Number(point.x)).filter(Number.isFinite);
  const ys = points.map((point) => Number(point.y)).filter(Number.isFinite);
  if (!xs.length || !ys.length) {
    return { x: 0, y: 0, width: 0, height: 0 };
  }

  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
}

function compareQuality(baselineQuality = null, candidateQuality = null) {
  const baselineConfidence = Number(baselineQuality?.overallConfidence ?? 0);
  const candidateConfidence = Number(candidateQuality?.overallConfidence ?? 0);
  const baselineGrade = baselineQuality?.grade || "-";
  const candidateGrade = candidateQuality?.grade || "-";
  const issueDelta = compareQualityIssueCodes(baselineQuality, candidateQuality);

  return {
    baselineGrade,
    candidateGrade,
    gradeChanged: baselineGrade !== candidateGrade,
    baselineConfidence,
    candidateConfidence,
    confidenceDelta: candidateConfidence - baselineConfidence,
    baselineIssueCount: baselineQuality?.issues?.length ?? 0,
    candidateIssueCount: candidateQuality?.issues?.length ?? 0,
    baselineScanRiskIssueCount: scanRiskIssues(baselineQuality).length,
    candidateScanRiskIssueCount: scanRiskIssues(candidateQuality).length,
    addedIssueCodes: issueDelta.addedCodes,
    removedIssueCodes: issueDelta.removedCodes,
    addedScanRiskCodes: issueDelta.addedCodes.filter((code) => String(code).toLowerCase().startsWith("quality.scan_risk.")),
    removedScanRiskCodes: issueDelta.removedCodes.filter((code) => String(code).toLowerCase().startsWith("quality.scan_risk."))
  };
}

function compareQualityIssueCodes(baselineQuality = null, candidateQuality = null) {
  const baselineCodes = issueCodeSet(qualityIssues(baselineQuality));
  const candidateCodes = issueCodeSet(qualityIssues(candidateQuality));
  return {
    addedCodes: [...candidateCodes].filter((code) => !baselineCodes.has(code)).slice(0, 8),
    removedCodes: [...baselineCodes].filter((code) => !candidateCodes.has(code)).slice(0, 8)
  };
}

function issueCodeSet(issues) {
  return new Set(
    issues
      .map((issue) => String(issue.code || issue.message || issue.severity || "quality"))
      .filter(Boolean)
      .sort((first, second) => String(first).localeCompare(String(second))));
}

function compareDiagnostics(baselineDiagnostics = null, candidateDiagnostics = null) {
  const baselineMessages = baselineDiagnostics?.messages ?? [];
  const candidateMessages = candidateDiagnostics?.messages ?? [];
  const baselineIndex = indexComparableItems(baselineMessages, "diagnostics");
  const candidateIndex = indexComparableItems(candidateMessages, "diagnostics");
  const addedMessages = [...candidateIndex.entries()]
    .filter(([key]) => !baselineIndex.has(key))
    .map(([, message]) => message);
  const removedMessages = [...baselineIndex.entries()]
    .filter(([key]) => !candidateIndex.has(key))
    .map(([, message]) => message);

  return {
    baselineCount: baselineMessages.length,
    candidateCount: candidateMessages.length,
    addedCount: addedMessages.length,
    removedCount: removedMessages.length,
    baselineWarningCount: baselineDiagnostics?.warningCount ?? countDiagnosticsBySeverity(baselineMessages, "warning"),
    candidateWarningCount: candidateDiagnostics?.warningCount ?? countDiagnosticsBySeverity(candidateMessages, "warning"),
    baselineErrorCount: baselineDiagnostics?.errorCount ?? countDiagnosticsBySeverity(baselineMessages, "error"),
    candidateErrorCount: candidateDiagnostics?.errorCount ?? countDiagnosticsBySeverity(candidateMessages, "error"),
    addedCodes: distinctDiagnosticCodes(addedMessages),
    removedCodes: distinctDiagnosticCodes(removedMessages)
  };
}

function countDiagnosticsBySeverity(messages, severity) {
  return messages.filter((message) => String(message.severity || "").toLowerCase() === severity).length;
}

function distinctDiagnosticCodes(messages) {
  return [...new Set(messages.map((message) => message.code || message.stage || message.message || "diagnostic"))].slice(0, 8);
}

function mergedSourceLayers(baseline, candidate) {
  const layers = new Map();
  [baseline, candidate].forEach((scan) => {
    (scan.layers ?? []).forEach((layer) => {
      const key = layerKey(layer.name);
      if (!key || layers.has(key)) {
        return;
      }

      layers.set(key, { ...layer });
    });
  });

  return [...layers.values()];
}

function setObjectGroups(scan = null) {
  elements.objectGroupList.replaceChildren();
  if (state.kvemo) {
    renderKvemoTopEntries(state.kvemo).forEach((item) => elements.objectGroupList.appendChild(item));
    return;
  }

  const aggregates = [...(scan?.objectAggregates ?? [])].sort(compareObjectAggregates);
  const groups = [...(scan?.objectGroups ?? [])].sort(compareObjectGroups);
  if (!aggregates.length && !groups.length) {
    elements.objectGroupList.textContent = "No object aggregates or groups";
    return;
  }

  aggregates.slice(0, 40).forEach((aggregate) => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = aggregate.requiresReview ? "object-group review" : "object-group";
    item.title = `Select ${objectAggregateLabel(aggregate)}`;

    const label = document.createElement("strong");
    label.textContent = objectAggregateLabel(aggregate);

    const detail = document.createElement("span");
    detail.textContent = [
      `${aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0} child object${(aggregate.childObjectCount ?? aggregate.childObjectIds?.length ?? 0) === 1 ? "" : "s"}`,
      aggregate.category || aggregate.kind || "",
      aggregate.routingInfluence ? `routing ${aggregate.routingInfluence}` : "",
      aggregate.confidence == null ? "" : `${Math.round(aggregate.confidence * 100)}%`
    ].filter(Boolean).join(" - ");

    const meta = document.createElement("small");
    meta.textContent = [
      aggregate.requiresReview ? "review" : "aggregate",
      aggregate.roomUseEvidence && aggregate.roomUseEvidence !== "Unknown" ? `room ${aggregate.roomUseEvidence}` : "",
      aggregate.sourceLayers?.length ? `${aggregate.sourceLayers.length} layers` : ""
    ].filter(Boolean).join(" - ");

    item.append(label, detail);
    if (meta.textContent) {
      item.appendChild(meta);
    }

    item.addEventListener("click", async () => {
      const selected = describeObjectAggregate(aggregate);
      state.selectedItem = selected;

      if (selected.pageNumber && selected.pageNumber !== state.currentPage) {
        state.currentPage = selected.pageNumber;
        await renderCurrentPage();
      }

      setSelection(selected);
    });

    elements.objectGroupList.appendChild(item);
  });

  groups.slice(0, 80).forEach((group) => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = group.requiresReview ? "object-group review" : "object-group";
    item.title = `Select ${objectGroupLabel(group)}`;

    const label = document.createElement("strong");
    label.textContent = objectGroupLabel(group);

    const detail = document.createElement("span");
    detail.textContent = [
      `${group.count ?? 0} candidate${group.count === 1 ? "" : "s"}`,
      group.category || group.kind || "",
      group.confidence == null ? "" : `${Math.round(group.confidence * 100)}%`
    ].filter(Boolean).join(" - ");

    const meta = document.createElement("small");
    meta.textContent = [
      group.requiresReview ? "review" : "",
      objectGroupPagesText(group),
      objectGroupLayersText(group)
    ].filter(Boolean).join(" - ");

    item.append(label, detail);
    if (meta.textContent) {
      item.appendChild(meta);
    }

    item.addEventListener("click", async () => {
      const selected = describeObjectGroup(group);
      state.selectedItem = selected;

      if (selected.pageNumber && selected.pageNumber !== state.currentPage) {
        state.currentPage = selected.pageNumber;
        await renderCurrentPage();
      }

      setSelection(selected);
    });

    elements.objectGroupList.appendChild(item);
  });

  if (aggregates.length > 40) {
    const overflow = document.createElement("small");
    overflow.className = "object-group-overflow";
    overflow.textContent = `${aggregates.length - 40} more aggregates available in JSON export`;
    elements.objectGroupList.appendChild(overflow);
  }

  if (groups.length > 80) {
    const overflow = document.createElement("small");
    overflow.className = "object-group-overflow";
    overflow.textContent = `${groups.length - 80} more groups available in JSON export`;
    elements.objectGroupList.appendChild(overflow);
  }
}

function compareObjectAggregates(first, second) {
  if (Boolean(first.requiresReview) !== Boolean(second.requiresReview)) {
    return first.requiresReview ? -1 : 1;
  }

  const routingDelta = String(first.routingInfluence || "").localeCompare(String(second.routingInfluence || ""));
  if (routingDelta !== 0) {
    return routingDelta;
  }

  const childDelta = (second.childObjectCount ?? second.childObjectIds?.length ?? 0) - (first.childObjectCount ?? first.childObjectIds?.length ?? 0);
  if (childDelta !== 0) {
    return childDelta;
  }

  const confidenceDelta = (second.confidence ?? 0) - (first.confidence ?? 0);
  if (confidenceDelta !== 0) {
    return confidenceDelta;
  }

  return objectAggregateLabel(first).localeCompare(objectAggregateLabel(second));
}

function objectAggregateLabel(aggregate) {
  return aggregate.label || aggregate.category || aggregate.kind || aggregate.id || "Object aggregate";
}

function objectAggregateCompositionSummary(aggregate) {
  const composition = aggregate?.composition || {};
  const categorySummary = formatKvemoCountSummary(composition.categoryCounts || []);
  const sourceSummary = formatKvemoCountSummary(composition.sourceKindCounts || []);
  return [
    categorySummary ? `categories ${categorySummary}` : "",
    sourceSummary ? `sources ${sourceSummary}` : ""
  ].filter(Boolean).join(" / ");
}

function knownValue(value) {
  return value && value !== "Unknown" ? value : "";
}

function compareObjectGroups(first, second) {
  if (Boolean(first.requiresReview) !== Boolean(second.requiresReview)) {
    return first.requiresReview ? -1 : 1;
  }

  const countDelta = (second.count ?? 0) - (first.count ?? 0);
  if (countDelta !== 0) {
    return countDelta;
  }

  const confidenceDelta = (second.confidence ?? 0) - (first.confidence ?? 0);
  if (confidenceDelta !== 0) {
    return confidenceDelta;
  }

  return objectGroupLabel(first).localeCompare(objectGroupLabel(second));
}

function objectGroupLabel(group) {
  return visualAiLabel(group) || group.label || group.symbolName || group.category || group.kind || group.signature || group.id || "Object group";
}

function visualAiLabel(item) {
  const ai = item?.visualAi;
  return ai?.label ? `${ai.label} (AI)` : "";
}

function formatVisualAi(ai) {
  if (!ai) {
    return "-";
  }

  return [
    ai.label || "unknown",
    ai.category || "",
    ai.confidence == null ? "" : formatPercent(ai.confidence),
    [ai.modelName, ai.modelVersion].filter(Boolean).join(" "),
    ai.inferenceEngine || ""
  ].filter(Boolean).join(" | ");
}

function firstObjectGroupPage(group) {
  return (group.pageNumbers ?? [])
    .map((page) => Number(page))
    .find((page) => Number.isFinite(page) && page > 0) ?? null;
}

function objectGroupPagesText(group) {
  const pages = [...new Set((group.pageNumbers ?? []).map((page) => Number(page)).filter((page) => Number.isFinite(page) && page > 0))];
  if (!pages.length) {
    return "";
  }

  return `page${pages.length === 1 ? "" : "s"} ${pages.join(", ")}`;
}

function objectGroupLayersText(group) {
  const layers = group.sourceLayers ?? [];
  if (!layers.length) {
    return "";
  }

  return layers.length === 1 ? `layer ${layers[0]}` : `${layers.length} layers`;
}

function buildObjectLabelProfileTemplate(scan) {
  const groups = [...(scan?.objectGroups ?? [])]
    .filter((group) => group.signature)
    .sort(compareObjectGroups);

  return {
    schemaVersion: "openplantrace.object-label-profile.v1",
    name: `${safeDocumentName()} object label draft`,
    version: "draft",
    rules: groups.map((group) => cleanObjectLabelRule({
      signature: group.signature,
      detectedTagPattern: commonDetectedTagPattern(group.detectedTags),
      category: group.category || "GenericSymbol",
      kind: group.kind || "Symbol",
      label: group.label || undefined,
      symbolName: group.symbolName || undefined,
      requiresReview: group.requiresReview ?? true,
      confidence: roundConfidence(group.confidence),
      evidence: [
        `Drafted from OpenPlanTrace viewer object group ${group.id || group.signature}.`,
        `${group.count ?? 0} candidate${group.count === 1 ? "" : "s"} in group.`,
        group.pageNumbers?.length ? `Pages ${group.pageNumbers.join(", ")}.` : "",
        group.sourceLayers?.length ? `Source layers: ${group.sourceLayers.join(", ")}.` : "",
        group.detectedTags?.length ? `Detected tags: ${group.detectedTags.join(", ")}.` : "",
        "Edit this rule's label/category/requiresReview fields before using it as confirmed knowledge."
      ].filter(Boolean)
    }))
  };
}

function buildKvemoObjectLabelProfileTemplate(kvemo) {
  const grouped = new Map();
  (kvemo?.entries ?? []).forEach((entry) => {
    const selector = kvemoProfileSelector(entry);
    if (!selector) {
      return;
    }

    const key = `${selector.kind}:${selector.value}`;
    if (!grouped.has(key)) {
      grouped.set(key, { selector, entries: [] });
    }

    grouped.get(key).entries.push(entry);
  });

  const rules = [...grouped.values()]
    .sort((first, second) => second.entries.length - first.entries.length || first.selector.value.localeCompare(second.selector.value))
    .map((group) => buildKvemoObjectLabelRule(group.selector, group.entries))
    .map(cleanObjectLabelRule);

  return {
    schemaVersion: "openplantrace.object-label-profile.v1",
    name: `${safeKvemoName()} Kvemo object label draft`,
    version: "draft",
    rules
  };
}

function kvemoProfileSelector(entry) {
  if (entry.groupSignature) {
    return { kind: "signature", value: entry.groupSignature };
  }

  if (entry.reviewKey && looksLikeObjectGroupSignature(entry.reviewKey)) {
    return { kind: "signature", value: entry.reviewKey };
  }

  const detectedTagPattern = commonDetectedTagPattern(entry.detectedTags);
  if (detectedTagPattern) {
    return { kind: "detectedTagPattern", value: detectedTagPattern };
  }

  return null;
}

function buildKvemoObjectLabelRule(selector, entries) {
  const classifications = entries.map((entry) => entry.visualAi).filter(Boolean);
  const bestClassification = classifications
    .slice()
    .sort((first, second) => Number(second.confidence || 0) - Number(first.confidence || 0))[0];
  const detectedTags = uniqueValues(entries.flatMap((entry) => entry.detectedTags || []));
  const sourceLayers = uniqueValues(entries.flatMap((entry) => entry.sourceEvidence?.layers || entry.sourceLayers || []));
  const blockNames = uniqueValues(entries.flatMap((entry) => entry.sourceEvidence?.blockNames || []));
  const pages = uniqueValues(entries.map((entry) => entry.pageNumber)).sort((first, second) => Number(first) - Number(second));
  const sourcePrimitiveCount = entries.reduce((sum, entry) => sum + (entry.sourcePrimitiveIds?.length || entry.sourceEvidence?.primitiveCount || 0), 0);
  const confidenceSource = bestClassification?.confidence ?? average(entries.map((entry) => entry.confidence).filter((value) => value != null));

  return {
    signature: selector.kind === "signature" ? selector.value : undefined,
    detectedTagPattern: commonDetectedTagPattern(detectedTags) || (selector.kind === "detectedTagPattern" ? selector.value : undefined),
    category: bestClassification?.category || mostCommonText(entries.map((entry) => entry.category)) || "GenericSymbol",
    kind: mostCommonText(entries.map((entry) => entry.candidateKind || entry.kind)) || "Symbol",
    label: bestClassification?.label || mostCommonText(entries.map((entry) => entry.label)),
    symbolName: mostCommonText(entries.map((entry) => entry.symbolName)),
    requiresReview: true,
    confidence: roundConfidence(confidenceSource),
    evidence: [
      `Drafted from Kvemo crop manifest review key '${selector.value}'.`,
      `${entries.length} crop${entries.length === 1 ? "" : "s"} share this selector.`,
      `Training use: ${formatValueCounts(entries.map((entry) => entry.suggestedTrainingUse))}.`,
      `Review priority: ${formatValueCounts(entries.map((entry) => entry.reviewPriority))}.`,
      "Edit this rule's label/category/requiresReview fields before using it as confirmed knowledge.",
      pages.length ? `Pages ${pages.join(", ")}.` : "",
      sourcePrimitiveCount ? `${sourcePrimitiveCount} source primitive${sourcePrimitiveCount === 1 ? "" : "s"} referenced by crops.` : "",
      detectedTags.length ? `Detected tags: ${detectedTags.slice(0, 12).join(", ")}${detectedTags.length > 12 ? ", ..." : ""}.` : "",
      sourceLayers.length ? `Source layers: ${sourceLayers.slice(0, 8).join(", ")}${sourceLayers.length > 8 ? ", ..." : ""}.` : "",
      blockNames.length ? `Block names: ${blockNames.slice(0, 8).join(", ")}${blockNames.length > 8 ? ", ..." : ""}.` : "",
      bestClassification ? `Kvemo model candidate: ${bestClassification.label || "unknown"} (${formatPercent(bestClassification.confidence || 0)}) using ${[bestClassification.modelName, bestClassification.modelVersion].filter(Boolean).join(" ") || "unknown model"}.` : ""
    ].filter(Boolean)
  };
}

function looksLikeObjectGroupSignature(value) {
  const text = String(value || "").toLowerCase();
  return text.includes("|kind:")
    || text.includes("|layers:")
    || text.startsWith("symbol:")
    || text.startsWith("geometry:");
}

function buildObjectReviewDataset(scan) {
  if (scan?.objectReviewDataset) {
    return normalizeObjectReviewDataset(scan.objectReviewDataset, scan);
  }

  const candidatesById = new Map((scan?.objects ?? []).map((candidate) => [candidate.id, candidate]));
  const groupedCandidateIds = new Set();
  const groups = [...(scan?.objectGroups ?? [])]
    .filter((group) => group.signature)
    .sort(compareObjectGroups)
    .map((group) => {
      const candidates = (group.candidateIds ?? [])
        .map((candidateId) => candidatesById.get(candidateId))
        .filter(Boolean)
        .map((candidate) => {
          groupedCandidateIds.add(candidate.id);
          return buildObjectReviewCandidate(candidate, group.id, scan);
        });
      const representativeBounds = normalizeRect(group.representativeBounds)
        ?? unionRects(candidates.map((candidate) => candidate.bounds))
        ?? { x: 0, y: 0, width: 0, height: 0 };
      const pageNumbers = normalizedPageNumbers(group.pageNumbers);
      const fallbackPageNumbers = normalizedPageNumbers(candidates.map((candidate) => candidate.pageNumber));
      const reviewPageNumber = pageNumbers[0] ?? fallbackPageNumbers[0] ?? firstObjectGroupPage(group) ?? 1;
      const candidateIds = (group.candidateIds ?? candidates.map((candidate) => candidate.candidateId))
        .filter(Boolean);

      return {
        groupId: group.id || group.signature,
        signature: group.signature,
        kind: group.kind || "Symbol",
        category: group.category || "GenericSymbol",
        count: group.count ?? candidates.length,
        representativeBounds,
        reviewCropBounds: normalizeRect(group.reviewCropBounds)
          ?? buildReviewCropBounds(representativeBounds, reviewPageNumber, scan)
          ?? clonePlain(representativeBounds),
        pageNumbers: pageNumbers.length ? pageNumbers : fallbackPageNumbers,
        candidateIds,
        sourcePrimitiveIds: group.sourcePrimitiveIds ?? [],
        sourceLayers: group.sourceLayers ?? [],
        requiresReview: group.requiresReview ?? true,
        confidence: roundConfidence(group.confidence) ?? 0,
        label: group.label || undefined,
        symbolName: group.symbolName || undefined,
        detectedTags: normalizeStringArray(group.detectedTags),
        suggestedRule: cleanObjectLabelRule({
          signature: group.signature,
          detectedTagPattern: commonDetectedTagPattern(group.detectedTags),
          category: group.category || "GenericSymbol",
          kind: group.kind || "Symbol",
          label: group.label || undefined,
          symbolName: group.symbolName || undefined,
          requiresReview: group.requiresReview ?? true,
          confidence: roundConfidence(group.confidence),
          evidence: [
            `Review dataset suggestion from viewer object group ${group.id || group.signature}.`,
            group.requiresReview ? "Human confirmation recommended before reusing this rule." : "Existing deterministic classification can be accepted or refined.",
            group.sourcePrimitiveIds?.length ? `${group.sourcePrimitiveIds.length} source primitive${group.sourcePrimitiveIds.length === 1 ? "" : "s"} represented.` : ""
          ].filter(Boolean)
        }),
        candidates,
        nearbyText: group.nearbyText ?? [],
        evidence: group.evidence ?? []
      };
    });

  const ungroupedCandidates = (scan?.objects ?? [])
    .filter((candidate) => !groupedCandidateIds.has(candidate.id))
    .map((candidate) => buildObjectReviewCandidate(candidate, null, scan));

  return {
    schemaVersion: currentObjectReviewDatasetSchemaVersion,
    name: `${safeDocumentName()} object review dataset`,
    version: "draft",
    generatedAt: new Date().toISOString(),
    documentId: scan?.documentId || "openplantrace-scan",
    sourceName: undefined,
    sourcePath: undefined,
    groups,
    ungroupedCandidates
  };
}

function commonDetectedTagPattern(tags) {
  const families = normalizeStringArray(tags)
    .map(tagFamily)
    .filter(Boolean);
  const distinct = [...new Set(families.map((family) => family.prefix.toUpperCase()))];
  if (distinct.length !== 1) {
    return undefined;
  }
  return families.every((family) => family.hasSeparator) ? `${distinct[0]}-*` : `${distinct[0]}*`;
}

function tagFamily(tag) {
  const text = String(tag || "").trim();
  const separated = /^([A-Za-z]+)[-_]/.exec(text);
  if (separated) {
    return { prefix: separated[1], hasSeparator: true };
  }
  const compact = /^([A-Za-z]+)([0-9][A-Za-z0-9]*)$/.exec(text);
  return compact ? { prefix: compact[1], hasSeparator: false } : undefined;
}

function average(values) {
  const numbers = values.map(Number).filter((value) => Number.isFinite(value));
  return numbers.length ? numbers.reduce((sum, value) => sum + value, 0) / numbers.length : undefined;
}

function mostCommonText(values) {
  const counts = new Map();
  normalizeStringArray(values).forEach((value) => {
    const key = value.toLowerCase();
    const current = counts.get(key);
    if (current) {
      current.count++;
    } else {
      counts.set(key, { value, count: 1 });
    }
  });

  return [...counts.values()]
    .sort((first, second) => second.count - first.count || first.value.localeCompare(second.value))[0]?.value;
}

function formatValueCounts(values) {
  const counts = new Map();
  normalizeStringArray(values).forEach((value) => {
    counts.set(value, (counts.get(value) || 0) + 1);
  });

  const parts = [...counts.entries()]
    .sort((first, second) => second[1] - first[1] || first[0].localeCompare(second[0]))
    .map(([value, count]) => `${value}:${count}`);
  return parts.length ? parts.join(", ") : "-";
}

function normalizeObjectReviewDataset(dataset, scan) {
  const groups = (dataset.groups ?? []).map((group) => {
    const candidates = (group.candidates ?? [])
      .map((candidate) => buildObjectReviewCandidate(candidate, group.groupId, scan));
    const representativeBounds = normalizeRect(group.representativeBounds)
      ?? unionRects(candidates.map((candidate) => candidate.bounds))
      ?? { x: 0, y: 0, width: 0, height: 0 };
    const pageNumbers = normalizedPageNumbers(group.pageNumbers);
    const fallbackPageNumbers = normalizedPageNumbers(candidates.map((candidate) => candidate.pageNumber));
    const reviewPageNumber = pageNumbers[0] ?? fallbackPageNumbers[0] ?? 1;
    const groupId = group.groupId || group.id || group.signature || `group-${reviewPageNumber}-${rectSignature(representativeBounds)}`;
    const signature = group.signature || groupId;

    return {
      ...group,
      groupId,
      signature,
      kind: group.kind || "Symbol",
      category: group.category || "GenericSymbol",
      count: group.count ?? candidates.length,
      representativeBounds,
      reviewCropBounds: normalizeRect(group.reviewCropBounds)
        ?? buildReviewCropBounds(representativeBounds, reviewPageNumber, scan)
        ?? clonePlain(representativeBounds),
      pageNumbers: pageNumbers.length ? pageNumbers : fallbackPageNumbers,
      candidateIds: group.candidateIds ?? candidates.map((candidate) => candidate.candidateId).filter(Boolean),
      sourcePrimitiveIds: group.sourcePrimitiveIds ?? [],
      sourceLayers: group.sourceLayers ?? [],
      requiresReview: group.requiresReview ?? true,
      confidence: roundConfidence(group.confidence) ?? 0,
      detectedTags: normalizeStringArray(group.detectedTags),
      suggestedRule: group.suggestedRule ?? cleanObjectLabelRule({
        signature,
        detectedTagPattern: commonDetectedTagPattern(group.detectedTags),
        category: group.category || "GenericSymbol",
        kind: group.kind || "Symbol",
        label: group.label || undefined,
        symbolName: group.symbolName || undefined,
        requiresReview: group.requiresReview ?? true,
        confidence: roundConfidence(group.confidence),
        evidence: [
          `Review dataset suggestion from viewer object review group ${groupId}.`,
          "Generated while upgrading an embedded review dataset to the current viewer schema."
        ]
      }),
      candidates,
      nearbyText: group.nearbyText ?? [],
      evidence: group.evidence ?? []
    };
  });

  return {
    ...dataset,
    schemaVersion: dataset.schemaVersion || currentObjectReviewDatasetSchemaVersion,
    name: dataset.name || `${safeDocumentName()} object review dataset`,
    version: dataset.version || "draft",
    generatedAt: dataset.generatedAt || new Date().toISOString(),
    documentId: dataset.documentId || scan?.documentId || "openplantrace-scan",
    groups,
    ungroupedCandidates: (dataset.ungroupedCandidates ?? [])
      .map((candidate) => buildObjectReviewCandidate(candidate, candidate.groupId ?? null, scan))
  };
}

function buildObjectCorrectionDataset(scan) {
  if (scan?.objectCorrectionDataset) {
    return normalizeObjectCorrectionDataset(scan.objectCorrectionDataset);
  }

  const reviewDataset = buildObjectReviewDataset(scan);
  const groups = [...(reviewDataset.groups ?? [])]
    .sort(compareReviewGroups);

  return {
    schemaVersion: "openplantrace.object-correction-dataset.v1",
    name: `${safeDocumentName()} object corrections`,
    version: "draft",
    createdAt: new Date().toISOString(),
    sourceReviewDatasetSchemaVersion: reviewDataset.schemaVersion || currentObjectReviewDatasetSchemaVersion,
    documentId: reviewDataset.documentId || scan?.documentId || "openplantrace-scan",
    sourceName: reviewDataset.sourceName || undefined,
    sourcePath: reviewDataset.sourcePath || undefined,
    actions: groups.map(buildObjectCorrectionGroupAction)
  };
}

function normalizeObjectCorrectionDataset(dataset) {
  return {
    ...dataset,
    actions: (dataset.actions ?? []).map((action) => ({
      ...action,
      detectedTags: normalizeStringArray(action.detectedTags)
    }))
  };
}

function compareReviewGroups(first, second) {
  if (Boolean(first.requiresReview) !== Boolean(second.requiresReview)) {
    return first.requiresReview ? -1 : 1;
  }

  const countDelta = (second.count ?? 0) - (first.count ?? 0);
  if (countDelta !== 0) {
    return countDelta;
  }

  return String(first.signature || first.groupId || "").localeCompare(String(second.signature || second.groupId || ""));
}

function buildObjectCorrectionGroupAction(group) {
  const tagPattern = commonDetectedTagPattern(group.detectedTags);
  return cleanObjectCorrectionAction({
    actionId: `group:${group.groupId || group.signature || "object-group"}`,
    targetKind: "Group",
    decision: "Unreviewed",
    applyScope: "MatchingSignature",
    groupId: group.groupId || undefined,
    signature: group.signature || undefined,
    originalKind: group.kind || "Symbol",
    originalCategory: group.category || "GenericSymbol",
    originalLabel: group.label || undefined,
    originalSymbolName: group.symbolName || undefined,
    correctedKind: group.kind || "Symbol",
    correctedCategory: group.category || "GenericSymbol",
    correctedLabel: group.label || undefined,
    correctedSymbolName: group.symbolName || undefined,
    requiresReview: group.requiresReview ?? true,
    confidence: roundConfidence(group.confidence) ?? 0,
    reviewCropBounds: normalizeRect(group.reviewCropBounds) ?? undefined,
    detectedTags: normalizeStringArray(group.detectedTags),
    pageNumbers: normalizedPageNumbers(group.pageNumbers),
    candidateIds: group.candidateIds ?? [],
    sourcePrimitiveIds: group.sourcePrimitiveIds ?? [],
    sourceLayers: group.sourceLayers ?? [],
    nearbyText: group.nearbyText ?? [],
    evidence: [
      `Drafted from OpenPlanTrace viewer object review group ${group.groupId || group.signature}.`,
      `${group.count ?? group.candidateIds?.length ?? 0} grouped candidate${(group.count ?? group.candidateIds?.length ?? 0) === 1 ? "" : "s"} share this signature.`,
      tagPattern ? `Common detected tag prefix can be promoted with applyScope MatchingDetectedTagPattern to create detectedTagPattern ${tagPattern}.` : "",
      "Change decision to Confirmed or Corrected before converting this action into reusable label rules.",
      ...(group.evidence ?? [])
    ].filter(Boolean)
  });
}

function buildObjectReviewCandidate(candidate, groupId, scan) {
  const bounds = normalizeRect(candidate.bounds)
    ?? boundsFromDetectionGeometry(candidate)
    ?? { x: 0, y: 0, width: 0, height: 0 };
  const pageNumber = normalizedPageNumber(candidate.pageNumber) ?? 1;
  const candidateId = candidate.candidateId || candidate.id || `candidate-${pageNumber}-${rectSignature(bounds)}`;

  return {
    ...candidate,
    candidateId,
    groupId: groupId || candidate.groupId || undefined,
    pageNumber,
    kind: candidate.kind || "Symbol",
    category: candidate.category || "Unknown",
    sourceKind: candidate.sourceKind || "Unknown",
    sourceWallComponentId: candidate.sourceWallComponentId || null,
    sourceWallComponentKind: candidate.sourceWallComponentKind || null,
    bounds,
    reviewCropBounds: normalizeRect(candidate.reviewCropBounds)
      ?? buildReviewCropBounds(bounds, pageNumber, scan)
      ?? clonePlain(bounds),
    confidence: roundConfidence(candidate.confidence) ?? 0,
    label: candidate.label || undefined,
    symbolName: candidate.symbolName || undefined,
    detectedTag: candidate.detectedTag || undefined,
    detectedTagSourcePrimitiveId: candidate.detectedTagSourcePrimitiveId || undefined,
    roomId: candidate.roomId || undefined,
    roomLabel: candidate.roomLabel || undefined,
    sourcePrimitiveIds: candidate.sourcePrimitiveIds ?? [],
    sourceLayers: candidate.sourceLayers ?? [],
    nearbyText: candidate.nearbyText ?? [],
    evidence: candidate.evidence ?? []
  };
}

function buildReviewCropBounds(bounds, pageNumber, scan, padding = defaultObjectReviewCropPadding) {
  const rect = normalizeRect(bounds);
  if (!rect) {
    return null;
  }

  const amount = Math.max(0, Number.isFinite(Number(padding)) ? Number(padding) : 0);
  const padded = {
    x: rect.x - amount,
    y: rect.y - amount,
    width: Math.max(0, rect.width + (amount * 2)),
    height: Math.max(0, rect.height + (amount * 2))
  };
  return clampRectToPage(padded, pageNumber, scan);
}

function clampRectToPage(bounds, pageNumber, scan) {
  const rect = normalizeRect(bounds);
  const pageBounds = pageBoundsForNumber(scan, pageNumber);
  if (!rect || !pageBounds) {
    return rect;
  }

  const pageRight = pageBounds.x + pageBounds.width;
  const pageBottom = pageBounds.y + pageBounds.height;
  const x = Math.max(pageBounds.x, Math.min(rect.x, pageRight));
  const y = Math.max(pageBounds.y, Math.min(rect.y, pageBottom));
  const right = Math.max(x, Math.min(rect.x + Math.max(0, rect.width), pageRight));
  const bottom = Math.max(y, Math.min(rect.y + Math.max(0, rect.height), pageBottom));
  return {
    x,
    y,
    width: Math.max(0, right - x),
    height: Math.max(0, bottom - y)
  };
}

function pageBoundsForNumber(scan, pageNumber) {
  const normalized = normalizedPageNumber(pageNumber);
  const page = scan?.pages?.find((item) => item.number === normalized) ?? scan?.pages?.[0];
  const width = Number(page?.width);
  const height = Number(page?.height);
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    return null;
  }

  return { x: 0, y: 0, width, height };
}

function normalizedPageNumbers(values) {
  const pages = Array.isArray(values) ? values : [];
  return [...new Set(pages.map(normalizedPageNumber).filter(Boolean))].sort((first, second) => first - second);
}

function normalizeStringArray(values) {
  if (typeof values === "string") {
    const value = values.trim();
    return value ? [value] : [];
  }

  const strings = Array.isArray(values) ? values : [];
  return [...new Set(strings.map((value) => String(value || "").trim()).filter(Boolean))];
}

function unionRects(rects) {
  const normalized = rects.map(normalizeRect).filter(Boolean);
  if (!normalized.length) {
    return null;
  }

  const minX = Math.min(...normalized.map((rect) => rect.x));
  const minY = Math.min(...normalized.map((rect) => rect.y));
  const maxX = Math.max(...normalized.map((rect) => rect.x + Math.max(0, rect.width)));
  const maxY = Math.max(...normalized.map((rect) => rect.y + Math.max(0, rect.height)));
  return {
    x: minX,
    y: minY,
    width: Math.max(0, maxX - minX),
    height: Math.max(0, maxY - minY)
  };
}

function cleanObjectLabelRule(rule) {
  return Object.fromEntries(Object.entries(rule).filter(([, value]) => value !== undefined && value !== null && value !== ""));
}

function cleanObjectCorrectionAction(action) {
  return Object.fromEntries(Object.entries(action).filter(([, value]) => value !== undefined && value !== null && value !== ""));
}

function roundConfidence(value) {
  if (value == null || !Number.isFinite(Number(value))) {
    return undefined;
  }

  return Math.round(Number(value) * 100) / 100;
}

function setDiagnostics(input = null) {
  elements.diagnosticsList.replaceChildren();

  const diagnostics = Array.isArray(input) ? { messages: input } : input;
  const messages = diagnostics?.messages ?? [];

  if (!messages.length) {
    if (diagnostics) {
      const summary = document.createElement("div");
      summary.className = "diagnostic-summary";
      summary.textContent = diagnosticSummaryText(diagnostics);
      elements.diagnosticsList.appendChild(summary);
      refreshWorkspaceTabs();
      return;
    }

    elements.diagnosticsList.textContent = "No diagnostics";
    refreshWorkspaceTabs();
    return;
  }

  if (diagnostics) {
    const summary = document.createElement("div");
    summary.className = "diagnostic-summary";
    summary.textContent = diagnosticSummaryText(diagnostics);
    elements.diagnosticsList.appendChild(summary);
  }

  messages.slice(0, 20).forEach((message) => {
    const item = document.createElement("div");
    item.className = `diagnostic-item ${String(message.severity || "").toLowerCase()}`;
    const title = document.createElement("strong");
    title.textContent = `${message.code || message.stage} - ${message.severity || "Info"}`;

    const body = document.createElement("span");
    body.textContent = message.message;

    const meta = document.createElement("small");
    const parts = [
      message.stage ? `stage ${message.stage}` : "",
      message.scope ? `scope ${message.scope}` : "",
      message.pageNumber ? `page ${message.pageNumber}` : "",
      message.confidence == null ? "" : `confidence ${Number(message.confidence).toFixed(2)}`,
      message.sourcePrimitiveIds?.length ? `${message.sourcePrimitiveIds.length} sources` : ""
    ].filter(Boolean);
    const properties = Object.entries(message.properties ?? {})
      .slice(0, 4)
      .map(([key, value]) => `${key}=${value}`);
    meta.textContent = [...parts, ...properties].join(" - ");

    item.append(title, body);
    if (meta.textContent) {
      item.appendChild(meta);
    }

    elements.diagnosticsList.appendChild(item);
  });
  refreshWorkspaceTabs();
}

function diagnosticSummaryText(diagnostics) {
  const stages = diagnostics.stages?.length ?? 0;
  const duration = diagnostics.durationMilliseconds == null ? "-" : `${formatNumber(diagnostics.durationMilliseconds)} ms`;
  return `${diagnostics.infoCount ?? 0} info - ${diagnostics.warningCount ?? 0} warnings - ${diagnostics.errorCount ?? 0} errors - ${stages} stages - ${duration}`;
}

function updateNavigation() {
  const total = pageCount();
  const scan = state.scan;
  const visualSnapshot = state.visualSnapshot;
  elements.pageLabel.textContent = `Page ${total ? state.currentPage : 0} / ${total}`;
  elements.prevPage.disabled = !total || state.currentPage <= 1;
  elements.nextPage.disabled = !total || state.currentPage >= total;
  elements.downloadJson.disabled = !scan && !visualSnapshot;
  elements.downloadLayers.disabled = scan
    ? !(scan.layers?.length > 0)
    : !(visualSnapshotCurrentPage(visualSnapshot)?.layers?.length > 0);
  elements.downloadCalibration.disabled = !scan || !scan.calibration;
  elements.downloadTitleBlocks.disabled = !scan || !(scan.titleBlocks?.length > 0);
  elements.downloadDimensions.disabled = !scan || !(scan.dimensions?.length > 0);
  elements.downloadGridAxes.disabled = !scan || !(scan.gridAxes?.length > 0);
  elements.downloadGridBays.disabled = !scan || !(scan.gridBaySpacings?.length > 0);
  elements.downloadAnnotations.disabled = !scan || !(scan.annotations?.length > 0);
  elements.downloadObjectGroups.disabled = !scan || !(scan.objectGroups?.length > 0);
  elements.downloadObjectLabelProfile.disabled = !((state.scan?.objectGroups?.length > 0) || (state.kvemo?.entries?.length > 0));
  elements.downloadObjectReviewDataset.disabled = !state.scan || !((state.scan.objectGroups?.length > 0) || (state.scan.objects?.length > 0) || state.scan.objectReviewDataset);
  elements.downloadObjectCorrectionDataset.disabled = !state.scan || !((state.scan.objectGroups?.length > 0) || state.scan.objectReviewDataset || state.scan.objectCorrectionDataset);
  elements.downloadBenchmark.disabled = !state.benchmarkManifest;
  elements.downloadSvg.disabled = !scan || !total;
}

function pageCount() {
  if (state.compare) {
    return Math.max(state.compare.baseline.pages.length, state.compare.candidate.pages.length);
  }

  return state.pdf?.numPages ?? state.scan?.pages?.length ?? state.visualSnapshot?.pages?.length ?? 0;
}

function pageExists(pageNumber) {
  return Boolean(
    state.scan?.pages?.some((item) => item.number === pageNumber)
    || state.visualSnapshot?.pages?.some((item) => item.number === pageNumber)
    || state.compare?.baseline?.pages?.some((item) => item.number === pageNumber)
    || state.compare?.candidate?.pages?.some((item) => item.number === pageNumber));
}

function currentPageDefinition() {
  return state.scan?.pages?.find((item) => item.number === state.currentPage)
    ?? state.visualSnapshot?.pages?.find((item) => item.number === state.currentPage)
    ?? state.compare?.baseline?.pages?.find((item) => item.number === state.currentPage)
    ?? state.scan?.pages?.[0]
    ?? state.visualSnapshot?.pages?.[0]
    ?? state.compare?.baseline?.pages?.[0]
    ?? null;
}

function describeMeasurement(item) {
  if (item.lengthMeters != null) {
    return `${formatNumber(item.lengthMeters)} m`;
  }

  if (item.areaSquareMeters != null) {
    return `${formatNumber(item.areaSquareMeters)} m2`;
  }

  if (item.widthMillimeters != null) {
    return `${formatNumber(item.widthMillimeters)} mm`;
  }

  if (item.distanceMeters != null) {
    return `${formatNumber(item.distanceMeters)} m`;
  }

  if (item.drawingDistance != null) {
    return `${formatNumber(item.drawingDistance)} drawing units`;
  }

  if (item.measuredMillimeters != null) {
    return `${formatNumber(item.measuredMillimeters)} mm`;
  }

  return "";
}

function describeOpeningPlacement(placement) {
  if (!placement) {
    return "";
  }

  const offsets = [
    placement.startOffsetDrawingUnits == null ? "" : `start ${formatNumber(placement.startOffsetDrawingUnits)} du`,
    placement.endOffsetDrawingUnits == null ? "" : `end ${formatNumber(placement.endOffsetDrawingUnits)} du`,
    placement.centerOffsetDrawingUnits == null ? "" : `center ${formatNumber(placement.centerOffsetDrawingUnits)} du`,
    placement.lengthDrawingUnits == null ? "" : `length ${formatNumber(placement.lengthDrawingUnits)} du`
  ].filter(Boolean).join(", ");
  const points = placement.startPoint && placement.endPoint
    ? `${formatPointCoordinates(placement.startPoint)} -> ${formatPointCoordinates(placement.endPoint)}`
    : "";
  const host = placement.hostWallId ? `host ${placement.hostWallId}` : "";

  return [host, offsets, points].filter(Boolean).join(" | ");
}

function describeMetricOpeningPlacement(placement) {
  if (!placement) {
    return "";
  }

  return [
    placement.startOffsetMillimeters == null ? "" : `start ${formatNumber(placement.startOffsetMillimeters)} mm`,
    placement.endOffsetMillimeters == null ? "" : `end ${formatNumber(placement.endOffsetMillimeters)} mm`,
    placement.centerOffsetMillimeters == null ? "" : `center ${formatNumber(placement.centerOffsetMillimeters)} mm`,
    placement.lengthMillimeters == null ? "" : `length ${formatNumber(placement.lengthMillimeters)} mm`
  ].filter(Boolean).join(", ");
}

function describeSwing(item) {
  if (!item.swingSide && !item.swingDirection && !item.hingeSide) {
    return "";
  }

  const parts = [item.hingeSide, item.swingSide, item.swingDirection]
    .filter((value) => value && value !== "Unknown");
  return parts.join(", ");
}

function formatNumber(value) {
  return Number(value).toLocaleString(undefined, {
    maximumFractionDigits: 3
  });
}

function formatCoordinateNumber(value, digits = 2) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return "-";
  }

  return number.toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits
  });
}

function formatPointCoordinates(point, digits = 1) {
  if (!point) {
    return "-";
  }

  const x = formatCoordinateNumber(point.x, digits);
  const y = formatCoordinateNumber(point.y, digits);
  return `x ${x}, y ${y}`;
}

function formatRectCoordinates(bounds, digits = 1) {
  const rect = normalizeRect(bounds);
  if (!rect) {
    return "-";
  }

  return `x ${formatCoordinateNumber(rect.x, digits)}, y ${formatCoordinateNumber(rect.y, digits)}, w ${formatCoordinateNumber(rect.width, digits)}, h ${formatCoordinateNumber(rect.height, digits)}`;
}

function formatLineCoordinates(line, digits = 1) {
  if (!line?.start || !line?.end) {
    return "-";
  }

  return `${formatPointCoordinates(line.start, digits)} -> ${formatPointCoordinates(line.end, digits)}`;
}

function formatAffineTransform(transform) {
  if (!Array.isArray(transform) || transform.length !== 6) {
    return "-";
  }

  return transform.map((value) => formatCoordinateNumber(value, 4)).join(", ");
}

function formatRealWorldPoint(point) {
  const millimetersPerUnit = state.scan?.coordinateSystem?.millimetersPerDrawingUnit ?? state.scan?.calibration?.millimetersPerDrawingUnit;
  if (!(millimetersPerUnit > 0)) {
    return "-";
  }

  return `${formatCoordinateNumber(point.x * millimetersPerUnit, 0)} mm, ${formatCoordinateNumber(point.y * millimetersPerUnit, 0)} mm`;
}

function formatRealWorldRect(bounds) {
  const rect = normalizeRect(bounds);
  const millimetersPerUnit = state.scan?.coordinateSystem?.millimetersPerDrawingUnit ?? state.scan?.calibration?.millimetersPerDrawingUnit;
  if (!rect || !(millimetersPerUnit > 0)) {
    return "-";
  }

  return [
    `x ${formatCoordinateNumber(rect.x * millimetersPerUnit, 0)} mm`,
    `y ${formatCoordinateNumber(rect.y * millimetersPerUnit, 0)} mm`,
    `w ${formatCoordinateNumber(rect.width * millimetersPerUnit, 0)} mm`,
    `h ${formatCoordinateNumber(rect.height * millimetersPerUnit, 0)} mm`
  ].join(", ");
}

function formatPercent(value) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return "-";
  }

  return `${(number * 100).toFixed(1)}%`;
}

function formatSigned(value, digits = 0) {
  const number = Number(value);
  const rounded = digits > 0 ? number.toFixed(digits) : String(Math.round(number));
  return number > 0 ? `+${rounded}` : rounded;
}

function formatMilliseconds(value) {
  if (value == null || value === "") {
    return "-";
  }

  const number = Number(value);
  if (!Number.isFinite(number)) {
    return "-";
  }

  const sign = number > 0 ? "+" : "";
  if (Math.abs(number) >= 1000) {
    return `${sign}${(number / 1000).toFixed(2)} s`;
  }

  return `${sign}${Math.round(number)} ms`;
}

function roundGeometryValue(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number.toFixed(1) : "0.0";
}

function layerKey(name) {
  return String(name || "").toLowerCase();
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function serializeCurrentOverlaySvg() {
  const page = currentPageDefinition();
  const clone = elements.overlay.cloneNode(true);
  clone.setAttribute("xmlns", "http://www.w3.org/2000/svg");
  clone.setAttribute("width", page.width);
  clone.setAttribute("height", page.height);
  clone.insertAdjacentHTML("afterbegin", `
    <defs>
      <style>
        .region{fill:rgba(20,124,114,.045);stroke:#147c72;stroke-width:1.1;vector-effect:non-scaling-stroke}
        .region.titleblock{fill:rgba(201,124,24,.10);stroke:#c97c18}
        .region.notes,.region.dimensions,.region.keyplan{fill:rgba(120,84,168,.09);stroke:#7854a8}
        .dimension{fill:rgba(120,84,168,.045);stroke:#7854a8;stroke-width:.85;stroke-dasharray:5 4;vector-effect:non-scaling-stroke}
        .dimension-line{stroke:#7854a8;stroke-width:.95;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .grid-axis{stroke:#6b7c1f;stroke-width:.8;stroke-dasharray:8 6;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .grid-bay{stroke:#437f97;stroke-width:.75;stroke-dasharray:3 5;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .grid-label{fill:#6b7c1f;font-size:12px;font-weight:700;paint-order:stroke;stroke:#fff;stroke-width:2}
        .annotation{fill:rgba(37,135,180,.055);stroke:#2587b4;stroke-width:.85;stroke-dasharray:3 3;vector-effect:non-scaling-stroke}
        .annotation-reference{fill:rgba(25,105,166,.10);stroke:#1969a6;stroke-width:1;vector-effect:non-scaling-stroke}
        .annotation-reference-link{stroke:#1969a6;stroke-width:.8;stroke-dasharray:4 4;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .wall{stroke:#7a5f18;stroke-width:1.35;stroke-linecap:butt;fill:none;vector-effect:non-scaling-stroke}
        .wall.wall-halo{stroke:#fff;stroke-width:3.9;stroke-opacity:.82;stroke-dasharray:none;pointer-events:none}
        .wall-exterior{stroke:#0f4fb8;stroke-width:1.9}
        .wall.wall-halo.wall-exterior{stroke-width:4.8}
        .wall-interior{stroke:#0f7a48;stroke-width:1.55}
        .wall.wall-halo.wall-interior{stroke-width:4.2}
        .wall-unknown{stroke:#7a5f18;stroke-width:1.35}
        .wall-object-like{stroke:#c97c18;stroke-width:1.05;stroke-dasharray:5 4}
        .wall-fragment{stroke:#7854a8;stroke-width:.9;stroke-dasharray:3 5}
        .wall-review{stroke-width:2.05;stroke-dasharray:3 2}
        .wall-topology-span-review-only{stroke:#a65f00;stroke-width:1.2;stroke-dasharray:3 3}
        .wall-blocked{stroke:#a52035;stroke-width:2.1;stroke-dasharray:1 3}
        .wall-excluded{stroke-width:.85;stroke-dasharray:2 6}
        .node{fill:rgba(255,255,255,.65);stroke:#b82f42;stroke-width:.42;vector-effect:non-scaling-stroke}
        .room{fill:rgba(63,143,87,.075);stroke:#3f8f57;stroke-width:.95;vector-effect:non-scaling-stroke}
        .room-cluster{fill:rgba(47,125,104,.035);stroke:#2f7d68;stroke-width:1;stroke-dasharray:9 6;vector-effect:non-scaling-stroke}
        .room-adjacency{stroke:#2f7d68;stroke-width:.85;stroke-dasharray:5 5;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .opening{fill:rgba(56,111,195,.10);stroke:#386fc3;stroke-width:.95;vector-effect:non-scaling-stroke}
        .opening-line{stroke:#285da8;stroke-width:.85;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .object{fill:rgba(201,124,24,.075);stroke:#c97c18;stroke-width:.9;vector-effect:non-scaling-stroke}
        .object-aggregate{fill:rgba(143,95,18,.045);stroke:#8f5f12;stroke-width:.75;stroke-dasharray:6 3;vector-effect:non-scaling-stroke}
        .surface-pattern{fill:rgba(15,107,120,.055);stroke:#0f6b78;stroke-width:.9;stroke-dasharray:5 4;vector-effect:non-scaling-stroke}
        .surface-pattern-label{fill:#0a5360;stroke:#fff;stroke-width:2.4;paint-order:stroke;font-size:10px;font-weight:700;letter-spacing:0;pointer-events:none}
        .routing-barrier{stroke:#0a5360;stroke-width:1.55;stroke-linecap:round;stroke-dasharray:2 3;fill:none;vector-effect:non-scaling-stroke}
        .routing-passage{stroke:#15803d;stroke-width:1.8;stroke-linecap:round;fill:none;vector-effect:non-scaling-stroke}
        .routing-obstacle{fill:rgba(120,84,168,.12);stroke:#7854a8;stroke-width:1.05;stroke-dasharray:4 3;vector-effect:non-scaling-stroke}
        .routing-obstacle-hard{fill:rgba(201,124,24,.14);stroke:#9a5d12}
        .routing-obstacle-structural{fill:rgba(196,61,61,.13);stroke:#b82f42}
        .routing-room-use{fill:rgba(15,107,120,.11);stroke:#0f6b78;stroke-width:1;stroke-dasharray:1 4;vector-effect:non-scaling-stroke}
        .routing-suppressed-object{fill:rgba(25,26,31,.025);stroke:#4f555f;stroke-width:.7;stroke-dasharray:2 5;vector-effect:non-scaling-stroke}
        .routing-ignored-object{fill:rgba(25,26,31,.018);stroke:#6f7782;stroke-width:.55;stroke-dasharray:1 5;vector-effect:non-scaling-stroke}
        .scan-review-queue{fill:rgba(196,61,61,.07);stroke:#c43d3d;stroke-width:1.2;stroke-dasharray:7 4;vector-effect:non-scaling-stroke}
        .scan-review-warning{fill:rgba(201,124,24,.10);stroke:#c97c18}
        .scan-review-info{fill:rgba(120,84,168,.08);stroke:#7854a8}
        .scan-review-kind-suppressed-wall-pattern-review{fill:rgba(15,107,120,.045);stroke:#0f6b78;stroke-width:.75;stroke-dasharray:2 4}
        .scan-review-kind-wall-graph-gap-review{fill:rgba(166,95,0,.055);stroke:#a65f00;stroke-width:.9;stroke-dasharray:3 3}
        .wall-graph-repair{stroke:#d04b24;stroke-width:.8;stroke-linecap:round;stroke-dasharray:2 2;fill:none;vector-effect:non-scaling-stroke}
        .wall-graph-repair-point{fill:#fff;stroke:#d04b24;stroke-width:.65;vector-effect:non-scaling-stroke}
        .scan-review-label{fill:#0f6b78;font-size:10px;font-weight:700;letter-spacing:0;paint-order:stroke;stroke:#fff;stroke-width:2;pointer-events:none}
        .compare-geometry{vector-effect:non-scaling-stroke}
        .compare-added{fill:rgba(63,143,87,.12);stroke:#1f8a51;stroke-width:1.25}
        .compare-removed{fill:rgba(196,61,61,.08);stroke:#b82f42;stroke-width:1.25;stroke-dasharray:9 5}
        .compare-line{fill:none;stroke-linecap:round}
        .compare-point{fill:#fff}
        .benchmark-target{fill:rgba(31,85,155,.07);stroke:#1f559b;stroke-width:1.1;stroke-dasharray:8 5;vector-effect:non-scaling-stroke}
        .benchmark-target.low,.benchmark-target.missing-evidence{fill:rgba(201,124,24,.10);stroke:#c97c18}
        .benchmark-target.bounds-edited{stroke-width:1.5}
        .benchmark-target.added{fill:rgba(31,85,155,.12);stroke:#1f559b}
        .benchmark-target.queue-precision-extra{fill:rgba(196,61,61,.08);stroke:#c43d3d}
        .benchmark-target.queue-spot-check-extra{fill:rgba(201,124,24,.10);stroke:#c97c18}
        .benchmark-target.queue-review-only{fill:rgba(120,84,168,.09);stroke:#7854a8}
        .benchmark-target.accepted{fill:rgba(63,143,87,.10);stroke:#3f8f57}
        .benchmark-target.rejected{fill:rgba(196,61,61,.08);stroke:#c43d3d;stroke-dasharray:3 5}
        .benchmark-target.needsReview{fill:rgba(201,124,24,.12);stroke:#c97c18}
        .benchmark-target.unpaged{stroke-dasharray:2 5}
        .benchmark-target-label{fill:#1f559b;font-size:11px;font-weight:700;paint-order:stroke;stroke:#fff;stroke-width:2}
        .benchmark-manual-draft{fill:rgba(25,26,31,.08);stroke:#191a1f;stroke-width:1.1;stroke-dasharray:5 4;vector-effect:non-scaling-stroke}
        .benchmark-manual-draft.drawing{fill:rgba(31,85,155,.10);stroke:#1f559b}
      </style>
    </defs>`);

  return `<?xml version="1.0" encoding="UTF-8"?>\n${new XMLSerializer().serializeToString(clone)}`;
}

function downloadBlob(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

function safeDocumentName() {
  const name = state.scan?.documentId || state.visualSnapshot?.documentId || "openplantrace";
  return name.replace(/\.[^.]+$/, "").replace(/[^a-z0-9_-]+/gi, "-").replace(/^-+|-+$/g, "") || "openplantrace";
}

function safeKvemoName() {
  const name = state.kvemo?.manifestName || "kvemo-crops";
  return String(name).replace(/\.[^.]+$/, "").replace(/[^a-z0-9_-]+/gi, "-").replace(/^-+|-+$/g, "") || "kvemo-crops";
}

function safeBenchmarkName() {
  const name = state.benchmarkManifest?.name || state.benchmarkManifest?.label || safeDocumentName();
  return String(name).replace(/\.[^.]+$/, "").replace(/[^a-z0-9_-]+/gi, "-").replace(/^-+|-+$/g, "") || "openplantrace-benchmark";
}

function cleanUrlLabel(value) {
  if (!value) {
    return "scan JSON";
  }

  if (String(value).startsWith("data:")) {
    return "embedded JSON";
  }

  try {
    const url = new URL(value, window.location.href);
    const name = url.pathname.split("/").filter(Boolean).at(-1);
    return decodeURIComponent(name || value);
  } catch {
    return String(value);
  }
}

function setEmptyStateMessage(title = "Select a PDF, placement JSON, scan JSON, or visual snapshot JSON", detail = "The scan overlay will appear here.") {
  const heading = elements.emptyState?.querySelector("strong");
  const body = elements.emptyState?.querySelector("span");
  if (heading) {
    heading.textContent = title;
  }

  if (body) {
    body.textContent = detail;
  }
}

function resetViewerState(sourceMode = "empty", options = {}) {
  if (!options.preserveBenchmark) {
    beginBenchmarkOverlayLoad();
  }

  const selectedItem = options.preserveSelection ? state.selectedItem : null;
  const benchmarkResult = options.preserveBenchmark ? state.benchmarkResult : null;
  const benchmarkManifest = options.preserveBenchmark ? state.benchmarkManifest : null;
  const benchmarkTargets = options.preserveBenchmark ? state.benchmarkTargets : [];
  const benchmarkReviewDecisions = options.preserveBenchmark ? state.benchmarkReviewDecisions : new Map();
  const benchmarkTargetEdits = options.preserveBenchmark ? state.benchmarkTargetEdits : new Map();
  const benchmarkDeletedTargets = options.preserveBenchmark ? state.benchmarkDeletedTargets : new Set();
  const benchmarkAddedTargetSequence = options.preserveBenchmark ? state.benchmarkAddedTargetSequence : 1;
  const pendingBenchmarkReviewSession = options.preserveBenchmark ? state.pendingBenchmarkReviewSession : null;
  const benchmarkFilters = options.preserveBenchmark ? { ...state.benchmarkFilters } : resetBenchmarkFilters();
  const benchmarkManualTargetDraft = options.preserveBenchmark
    ? resetBenchmarkManualTargetDraft(state.benchmarkManualTargetDraft)
    : resetBenchmarkManualTargetDraft();

  state.pdf = null;
  state.scan = null;
  state.visualSnapshot = null;
  state.placement = null;
  state.currentPage = 1;
  state.sourceMode = sourceMode;
  state.enabledSourceLayers = new Set();
  state.compare = null;
  state.benchmarkComparison = null;
  state.batchComparison = null;
  state.benchmarkResult = benchmarkResult;
  state.benchmarkManifest = benchmarkManifest;
  state.benchmarkTargets = benchmarkTargets;
  state.benchmarkReviewDecisions = benchmarkReviewDecisions;
  state.benchmarkTargetEdits = benchmarkTargetEdits;
  state.benchmarkDeletedTargets = benchmarkDeletedTargets;
  state.benchmarkAddedTargetSequence = benchmarkAddedTargetSequence;
  state.pendingBenchmarkReviewSession = pendingBenchmarkReviewSession;
  state.benchmarkFilters = benchmarkFilters;
  state.benchmarkManualTargetDraft = benchmarkManualTargetDraft;
  state.benchmarkDrawBox = null;
  state.benchmarkSuppressNextOverlayClick = false;
  releaseKvemoObjectUrls();
  state.kvemo = null;
  state.kvemoFilters = resetKvemoFilters();
  state.selectedItem = selectedItem;
  setEmptyStateMessage();
  if (elements.kvemoReview) {
    elements.kvemoReview.hidden = true;
    elements.kvemoReview.replaceChildren();
  }
  setCounts();
  setDiagnostics();
  setSourceLayers();
  setCalibration();
  setQuality();
  setTitleBlocks();
  setObjectGroups();
  setCompare();
  setBenchmarkDetails();
  setSelection(state.selectedItem);
  clearOverlay();
  setSourceUnderlayBadge();
  updateNavigation();
}

function isPlacementPayload(payload) {
  return String(payload?.schemaVersion || "").startsWith("openplantrace.placement.")
    || (payload?.qualityGate
      && payload?.coordinateSystem
      && Array.isArray(payload?.pages)
      && Array.isArray(payload?.walls)
      && Array.isArray(payload?.rooms)
      && Array.isArray(payload?.openings));
}

function isVisualSnapshotPayload(payload) {
  const schemaVersion = String(payload?.schemaVersion || "");
  return schemaVersion.startsWith("openplantrace.visual-snapshot.")
    || (payload?.coordinateSpace === "OpenPlanTracePageCoordinates"
      && Array.isArray(payload?.pages)
      && payload.pages.some((page) => Array.isArray(page?.layers)));
}

function normalizeVisualSnapshotPayload(payload, label = "") {
  if (!isVisualSnapshotPayload(payload)) {
    throw new Error("JSON is not an OpenPlanTrace visual snapshot.");
  }

  const pages = (Array.isArray(payload.pages) ? payload.pages : [])
    .map((page, index) => normalizeVisualSnapshotPage(page, index))
    .filter(Boolean);

  if (!pages.length) {
    throw new Error("Visual snapshot JSON does not contain any pages.");
  }

  return {
    schemaVersion: payload.schemaVersion ?? "",
    documentId: payload.documentId ?? label ?? "openplantrace-visual-snapshot",
    label,
    coordinateSpace: payload.coordinateSpace ?? "OpenPlanTracePageCoordinates",
    origin: payload.origin ?? "TopLeft",
    xAxisDirection: payload.xAxisDirection ?? "Right",
    yAxisDirection: payload.yAxisDirection ?? "Down",
    unit: payload.unit ?? "DrawingUnit",
    scanSchemaVersion: payload.scanSchemaVersion ?? "",
    qualityGrade: payload.qualityGrade ?? "Unknown",
    qualityConfidence: nullableFiniteNumber(payload.qualityConfidence),
    requiresReview: Boolean(payload.requiresReview),
    reviewQueueCount: nonNegativeInteger(payload.reviewQueueCount),
    reviewQueueKindBreakdown: payload.reviewQueueKindBreakdown ?? {},
    reviewQueueSeverityBreakdown: payload.reviewQueueSeverityBreakdown ?? {},
    issues: Array.isArray(payload.issues) ? payload.issues : [],
    pages
  };
}

function normalizeVisualSnapshotPage(page, index) {
  if (!page || typeof page !== "object") {
    return null;
  }

  const pageBoundsInput = normalizeRect(page.pageBounds);
  const width = finiteNumber(page.width, pageBoundsInput?.width ?? 0);
  const height = finiteNumber(page.height, pageBoundsInput?.height ?? 0);
  const pageBounds = pageBoundsInput ?? (width > 0 && height > 0 ? { x: 0, y: 0, width, height } : null);
  const resolvedWidth = width > 0 ? width : pageBounds?.width ?? 0;
  const resolvedHeight = height > 0 ? height : pageBounds?.height ?? 0;
  if (!(resolvedWidth > 0) || !(resolvedHeight > 0)) {
    return null;
  }

  const pageNumber = normalizedPageNumber(page.pageNumber ?? page.number) ?? index + 1;
  const normalizedPageBounds = pageBounds ?? { x: 0, y: 0, width: resolvedWidth, height: resolvedHeight };
  const layers = (Array.isArray(page.layers) ? page.layers : [])
    .map((layer) => normalizeVisualSnapshotLayer(layer, normalizedPageBounds))
    .filter(Boolean);

  return {
    ...page,
    number: pageNumber,
    pageNumber,
    width: resolvedWidth,
    height: resolvedHeight,
    pageBounds: normalizedPageBounds,
    detectionBounds: normalizeRect(page.detectionBounds),
    detectionCoverage: nullableFiniteNumber(page.detectionCoverage),
    drawableItemCount: nonNegativeInteger(page.drawableItemCount),
    primitiveCount: nonNegativeInteger(page.primitiveCount),
    reviewQueueCount: nonNegativeInteger(page.reviewQueueCount),
    svgPath: page.svgPath || "",
    layers,
    issues: Array.isArray(page.issues) ? page.issues : [],
    reviewQueue: Array.isArray(page.reviewQueue) ? page.reviewQueue : []
  };
}

function normalizeVisualSnapshotLayer(layer, pageBounds) {
  if (!layer || typeof layer !== "object") {
    return null;
  }

  const bounds = normalizeRect(layer.bounds);
  const normalizedBounds = normalizeRect(layer.normalizedBounds)
    ?? normalizedBoundsFromPage(bounds, pageBounds);
  const count = nonNegativeInteger(layer.count);
  const normalizedDensity = nullableFiniteNumber(layer.normalizedDensity)
    ?? normalizedLayerDensity(count, normalizedBounds);

  return {
    ...layer,
    name: String(layer.name || "layer"),
    count,
    bounds,
    normalizedBounds,
    normalizedDensity,
    averageConfidence: nullableFiniteNumber(layer.averageConfidence),
    minimumConfidence: nullableFiniteNumber(layer.minimumConfidence),
    maximumConfidence: nullableFiniteNumber(layer.maximumConfidence),
    breakdown: layer.breakdown && typeof layer.breakdown === "object" ? layer.breakdown : {}
  };
}

function normalizedBoundsFromPage(bounds, pageBounds) {
  const rect = normalizeRect(bounds);
  const page = normalizeRect(pageBounds);
  if (!rect || !page || !(page.width > 0) || !(page.height > 0)) {
    return null;
  }

  return {
    x: (rect.x - page.x) / page.width,
    y: (rect.y - page.y) / page.height,
    width: rect.width / page.width,
    height: rect.height / page.height
  };
}

function normalizedLayerDensity(count, normalizedBounds) {
  const bounds = normalizeRect(normalizedBounds);
  if (!(count > 0) || !bounds) {
    return 0;
  }

  const area = Math.abs(bounds.width * bounds.height);
  const densityArea = area <= 0 ? 0.0001 : area;
  return Math.round((count / densityArea) * 1000) / 1000;
}

function normalizeScanPayload(payload) {
  if (isPlacementPayload(payload)) {
    return normalizePlacementPayload(payload);
  }

  const pages = payload.pages ?? [];
  if (!Array.isArray(pages) || pages.length === 0) {
    throw new Error("Scan JSON does not contain any pages.");
  }

  const calibration = payload.calibration ?? null;
  return {
    schemaVersion: payload.schemaVersion ?? "",
    documentId: payload.documentId ?? payload.document?.id ?? payload.document?.sourceName ?? "openplantrace-scan",
    document: payload.document ?? null,
    pages,
    coordinateSystem: normalizeCoordinateSystem(payload.coordinateSystem, pages, calibration),
    layers: payload.layers ?? payload.layerAnalysis?.layers ?? [],
    calibration,
    measurementConsistency: payload.measurementConsistency ?? null,
    importReadiness: payload.importReadiness ?? null,
    titleBlocks: payload.titleBlocks ?? [],
    dimensions: payload.dimensions ?? [],
    gridAxes: payload.gridAxes ?? [],
    gridBaySpacings: payload.gridBaySpacings ?? [],
    annotations: payload.annotations ?? [],
    regions: payload.regions ?? [],
    surfacePatterns: payload.surfacePatterns ?? [],
    walls: normalizeWallItems(payload.walls ?? []),
    wallComponents: payload.wallComponents ?? payload.wallGraph?.components ?? [],
    nodes: payload.nodes ?? payload.wallGraph?.nodes ?? [],
    edges: payload.edges ?? payload.wallGraph?.edges ?? [],
    wallGraphRepairCandidates: payload.wallGraphRepairCandidates ?? payload.wallGraph?.repairCandidates ?? [],
    rooms: payload.rooms ?? [],
    roomAdjacencyEdges: payload.roomAdjacencyEdges ?? payload.roomAdjacencyGraph?.edges ?? [],
    roomClusters: payload.roomClusters ?? payload.roomAdjacencyGraph?.clusters ?? [],
    openings: payload.openings ?? [],
    objects: payload.objects ?? [],
    objectGroups: payload.objectGroups ?? [],
    objectAggregates: payload.objectAggregates ?? [],
    reviewQueue: payload.reviewQueue ?? [],
    routingLayer: normalizeRoutingLayer(payload.routingLayer),
    objectReviewDataset: payload.objectReviewDataset ?? null,
    objectCorrectionDataset: payload.objectCorrectionDataset ?? null,
    quality: payload.quality ?? null,
    diagnostics: payload.diagnostics ?? null
  };
}

function normalizePlacementPayload(payload) {
  if (!isPlacementPayload(payload)) {
    throw new Error("JSON is not an OpenPlanTrace placement packet.");
  }

  const pages = normalizePlacementPages(payload.pages);
  if (!pages.length) {
    throw new Error("Placement JSON does not contain any pages.");
  }

  const calibration = normalizePlacementCalibration(payload.calibration);
  const quality = normalizePlacementQuality(payload, calibration);
  return {
    schemaVersion: payload.schemaVersion || "openplantrace.placement.v4",
    scanSchemaVersion: payload.scanSchemaVersion || "",
    document: payload.document ?? null,
    documentId: payload.document?.id ?? payload.document?.sourceName ?? "openplantrace-placement",
    pages,
    coordinateSystem: normalizeCoordinateSystem(payload.coordinateSystem, pages, calibration),
    layers: [],
    calibration,
    measurementConsistency: normalizePlacementMeasurementConsistency(payload.calibration),
    titleBlocks: [],
    dimensions: [],
    gridAxes: [],
    gridBaySpacings: [],
    annotations: [],
    regions: [],
    surfacePatterns: (payload.surfacePatterns ?? []).map(normalizePlacementItem),
    walls: normalizeWallItems(payload.walls ?? []),
    wallComponents: (payload.wallGraph?.components ?? []).map(normalizePlacementItem),
    nodes: payload.wallGraph?.nodes ?? [],
    edges: (payload.wallGraph?.edges ?? []).map(normalizePlacementItem),
    wallGraphRepairCandidates: (payload.wallGraphRepairCandidates ?? []).map(normalizePlacementItem),
    rooms: (payload.rooms ?? []).map(normalizePlacementItem),
    roomAdjacencyEdges: [],
    roomClusters: [],
    openings: (payload.openings ?? []).map(normalizePlacementItem),
    objects: [],
    objectGroups: [],
    objectAggregates: (payload.objectAggregates ?? []).map(normalizePlacementItem),
    reviewQueue: [],
    routingLayer: normalizeRoutingLayer(payload.routingLayer),
    objectReviewDataset: null,
    objectCorrectionDataset: null,
    quality,
    diagnostics: normalizePlacementDiagnostics(payload, quality),
    placementSource: payload
  };
}

function normalizePlacementPages(pages) {
  return (Array.isArray(pages) ? pages : [])
    .map((page, index) => {
      const number = normalizedPageNumber(page?.number ?? page?.pageNumber) ?? index + 1;
      const width = Number(page?.width ?? page?.bounds?.width);
      const height = Number(page?.height ?? page?.bounds?.height);
      if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
        return null;
      }

      return {
        ...page,
        number,
        pageNumber: number,
        width,
        height,
        bounds: normalizeRect(page?.bounds) ?? { x: 0, y: 0, width, height }
      };
    })
    .filter(Boolean);
}

function normalizePlacementItem(item) {
  return {
    ...item,
    pageNumber: normalizedPageNumber(item?.pageNumber) ?? 1,
    bounds: normalizeRect(item?.bounds),
    centerLine: normalizeLine(item?.centerLine) ?? item?.centerLine ?? null,
    line: normalizeLine(item?.line) ?? item?.line ?? null,
    boundary: Array.isArray(item?.boundary) ? item.boundary : [],
    sourcePrimitiveIds: item?.sourcePrimitiveIds ?? [],
    sourceLayers: item?.sourceLayers ?? [],
    evidence: item?.evidence ?? []
  };
}

function normalizeWallItems(items) {
  return (Array.isArray(items) ? items : [])
    .map(normalizeWallItem)
    .filter(Boolean);
}

function normalizeWallItem(item) {
  if (!item || typeof item !== "object") {
    return null;
  }

  return {
    ...normalizePlacementItem(item),
    pairEvidence: normalizeWallPairEvidence(item.pairEvidence),
    topologySpans: normalizeWallTopologySpans(item.topologySpans),
    solidSpans: normalizeWallSolidSpans(item.solidSpans)
  };
}

function normalizeWallPairEvidence(pairEvidence) {
  if (!pairEvidence || typeof pairEvidence !== "object") {
    return null;
  }

  return {
    ...pairEvidence,
    firstFaceLine: normalizeLine(pairEvidence.firstFaceLine),
    secondFaceLine: normalizeLine(pairEvidence.secondFaceLine)
  };
}

function normalizeWallTopologySpans(spans) {
  return (Array.isArray(spans) ? spans : [])
    .map(normalizeWallTopologySpan)
    .filter(Boolean);
}

function normalizeWallTopologySpan(span) {
  if (!span || typeof span !== "object") {
    return null;
  }

  const centerLine = normalizeLine(span.centerLine ?? span.line);
  if (!centerLine) {
    return null;
  }

  return {
    ...span,
    pageNumber: normalizedPageNumber(span.pageNumber) ?? 1,
    centerLine,
    line: centerLine,
    bounds: normalizeRect(span.bounds) ?? boundsFromLine(centerLine),
    sourcePrimitiveIds: span.sourcePrimitiveIds ?? [],
    sourceLayers: span.sourceLayers ?? [],
    evidence: span.evidence ?? []
  };
}

function normalizeWallSolidSpans(spans) {
  return (Array.isArray(spans) ? spans : [])
    .map(normalizeWallSolidSpan)
    .filter(Boolean);
}

function normalizeWallSolidSpan(span) {
  if (!span || typeof span !== "object") {
    return null;
  }

  const centerLine = normalizeLine(span.centerLine ?? span.line);
  const bodyPolygon = normalizePointArray(span.bodyPolygon);
  if (!centerLine && bodyPolygon.length < 3) {
    return null;
  }

  return {
    ...span,
    pageNumber: normalizedPageNumber(span.pageNumber) ?? 1,
    centerLine,
    line: centerLine,
    bodyPolygon,
    bodyBounds: normalizeRect(span.bodyBounds) ?? (bodyPolygon.length ? boundsFromPoints(bodyPolygon) : null),
    bounds: normalizeRect(span.bounds) ?? normalizeRect(span.bodyBounds) ?? (bodyPolygon.length ? boundsFromPoints(bodyPolygon) : null),
    evidence: span.evidence ?? []
  };
}

function normalizePointArray(points) {
  return (Array.isArray(points) ? points : [])
    .map(normalizePoint)
    .filter(Boolean);
}

function normalizePlacementCalibration(calibration = null) {
  if (!calibration) {
    return null;
  }

  return {
    ...calibration,
    sourceUnit: calibration.drawingUnit || calibration.sourceUnit || "drawing-unit",
    realWorldUnit: calibration.realWorldUnit || "Unknown",
    confidence: calibration.measurementConfidence ?? calibration.confidence ?? null,
    evidence: (calibration.evidence ?? []).map((item) =>
      typeof item === "string" ? { description: item } : item)
  };
}

function normalizePlacementMeasurementConsistency(calibration = null) {
  if (!calibration) {
    return null;
  }

  return {
    hasReliableCalibration: Boolean(calibration.hasReliableMeasurementScale),
    checkedCount: calibration.measurementCheckedCount ?? 0,
    consistentCount: calibration.measurementConsistentCount ?? 0,
    outlierCount: calibration.measurementOutlierCount ?? 0,
    selectedMillimetersPerDrawingUnit: calibration.millimetersPerDrawingUnit ?? null,
    medianDimensionMillimetersPerDrawingUnit: null,
    dimensionScaleSpreadRatio: null,
    confidence: calibration.measurementConfidence ?? null,
    checks: []
  };
}

function normalizePlacementQuality(payload, calibration = null) {
  const gate = payload.qualityGate ?? {};
  const issues = (payload.issues ?? []).map(normalizePlacementIssue);
  const detectionCount = (payload.walls?.length ?? 0)
    + (payload.surfacePatterns?.length ?? 0)
    + (payload.rooms?.length ?? 0)
    + (payload.openings?.length ?? 0)
    + (payload.objectAggregates?.length ?? 0)
    + routingLayerItemCount(normalizeRoutingLayer(payload.routingLayer));

  return {
    grade: gate.qualityGrade || "Unknown",
    overallConfidence: gate.qualityConfidence ?? null,
    requiresReview: Boolean(gate.requiresReview),
    detectorCount: 6,
    detectorWithFindingsCount: [
      payload.walls,
      payload.surfacePatterns,
      payload.rooms,
      payload.openings,
      payload.objectAggregates,
      payload.routingLayer?.barriers
    ].filter((items) => (items?.length ?? 0) > 0).length,
    detectionCount,
    diagnosticWarningCount: gate.diagnosticWarningCount ?? issues.filter((issue) => placementSeverityRank(issue.severity) === "Warning").length,
    diagnosticErrorCount: gate.diagnosticErrorCount ?? issues.filter((issue) => placementSeverityRank(issue.severity) === "Error").length,
    issues,
    placementGate: {
      coordinateTrust: gate.coordinateTrust || "Unknown",
      metricTrust: gate.metricTrust || "Unknown",
      readyForCoordinatePlacement: Boolean(gate.readyForCoordinatePlacement),
      readyForMetricPlacement: Boolean(gate.readyForMetricPlacement),
      hasReliableCalibration: Boolean(gate.hasReliableCalibration ?? calibration?.hasReliableMeasurementScale)
    },
    detectors: [
      placementDetectorSummary("Walls", payload.walls),
      placementDetectorSummary("Surface patterns", payload.surfacePatterns),
      placementDetectorSummary("Rooms", payload.rooms),
      placementDetectorSummary("Openings", payload.openings),
      placementDetectorSummary("Object aggregates", payload.objectAggregates),
      placementDetectorSummary("Routing", [
        ...(payload.routingLayer?.barriers ?? []),
        ...(payload.routingLayer?.passages ?? []),
        ...(payload.routingLayer?.obstacles ?? []),
        ...(payload.routingLayer?.roomUseHints ?? []),
        ...(payload.routingLayer?.suppressedObjects ?? []),
        ...(payload.routingLayer?.ignoredObjects ?? [])
      ])
    ].filter(Boolean)
  };
}

function placementDetectorSummary(name, items = []) {
  const list = Array.isArray(items) ? items : [];
  if (!list.length) {
    return null;
  }

  const confidences = list
    .map((item) => Number(item?.confidence ?? item?.reliability?.confidence))
    .filter(Number.isFinite);
  const averageConfidence = confidences.length
    ? confidences.reduce((total, value) => total + value, 0) / confidences.length
    : null;

  return {
    name,
    itemCount: list.length,
    averageConfidence,
    reviewRequiredCount: list.filter((item) => item?.requiresReview || item?.reliability?.requiresReview).length
  };
}

function normalizePlacementDiagnostics(payload, quality) {
  const issueMessages = (payload.issues ?? []).map((issue) => ({
    severity: placementSeverityRank(issue.severity),
    stage: "placement",
    scope: issue.itemId || "",
    code: issue.code || "placement.issue",
    message: issue.message || "",
    pageNumber: issue.pageNumber ?? null,
    confidence: issue.confidence ?? null,
    sourcePrimitiveIds: [],
    properties: issue.properties ?? {}
  }));

  const gateEvidence = (payload.qualityGate?.evidence ?? []).map((message) => ({
    severity: "Info",
    stage: "placement",
    scope: "qualityGate",
    code: "placement.quality_gate.evidence",
    message,
    properties: {
      coordinateTrust: payload.qualityGate?.coordinateTrust || "-",
      metricTrust: payload.qualityGate?.metricTrust || "-"
    }
  }));

  const messages = [...issueMessages, ...gateEvidence];
  return {
    infoCount: messages.filter((item) => item.severity === "Info").length,
    warningCount: messages.filter((item) => item.severity === "Warning").length,
    errorCount: messages.filter((item) => item.severity === "Error").length,
    durationMilliseconds: 0,
    stages: ["placement"],
    messages,
    placementGate: quality?.placementGate ?? null
  };
}

function normalizePlacementIssue(issue) {
  return {
    code: issue?.code || "placement.issue",
    severity: placementSeverityRank(issue?.severity),
    message: issue?.message || "",
    pageNumber: issue?.pageNumber ?? null,
    itemId: issue?.itemId ?? null,
    bounds: normalizeRect(issue?.bounds),
    boundsMillimeters: normalizeRect(issue?.boundsMillimeters),
    confidence: Number.isFinite(Number(issue?.confidence)) ? Number(issue.confidence) : null,
    recommendedAction: issue?.recommendedAction || "",
    sourcePrimitiveIds: normalizeStringArray(issue?.sourcePrimitiveIds),
    sourceLayers: normalizeStringArray(issue?.sourceLayers),
    evidence: normalizeStringArray(issue?.evidence),
    properties: issue?.properties ?? {}
  };
}

function placementSeverityRank(value) {
  switch (String(value || "").toLowerCase()) {
    case "error":
    case "critical":
      return "Error";
    case "warning":
    case "warn":
      return "Warning";
    default:
      return "Info";
  }
}

function normalizeRoutingLayer(layer) {
  return {
    barriers: layer?.barriers ?? [],
    passages: layer?.passages ?? [],
    obstacles: layer?.obstacles ?? [],
    roomUseHints: layer?.roomUseHints ?? [],
    suppressedObjects: layer?.suppressedObjects ?? [],
    ignoredObjects: layer?.ignoredObjects ?? [],
    suppressedObjectCandidateIds: layer?.suppressedObjectCandidateIds ?? [],
    ignoredObjectCandidateIds: layer?.ignoredObjectCandidateIds ?? [],
    evidence: layer?.evidence ?? []
  };
}

function normalizeCoordinateSystem(coordinateSystem, pages, calibration = null) {
  if (coordinateSystem && Array.isArray(coordinateSystem.pageFrames) && coordinateSystem.pageFrames.length) {
    return {
      ...coordinateSystem,
      coordinateSpace: coordinateSystem.coordinateSpace || "OpenPlanTracePageCoordinates",
      unit: coordinateSystem.unit || "drawing-unit",
      origin: coordinateSystem.origin || "TopLeft",
      xAxisDirection: coordinateSystem.xAxisDirection || "Right",
      yAxisDirection: coordinateSystem.yAxisDirection || "Down",
      coordinateOrder: coordinateSystem.coordinateOrder || "x,y",
      pageFrames: coordinateSystem.pageFrames
    };
  }

  return {
    coordinateSpace: "OpenPlanTracePageCoordinates",
    unit: "drawing-unit",
    origin: "TopLeft",
    xAxisDirection: "Right",
    yAxisDirection: "Down",
    geometryBasis: "PDF/DXF page coordinate space after OpenPlanTrace normalization",
    coordinateOrder: "x,y",
    boundsKind: "x,y,width,height for rectangles; start/end for lines; ordered point arrays for polygons",
    precision: "double",
    realWorldUnit: calibration?.realWorldUnit || "Unknown",
    millimetersPerDrawingUnit: calibration?.millimetersPerDrawingUnit ?? null,
    note: "Coordinates are page-local drawing units, not screen pixels and not WGS84.",
    pageFrames: pages.map((page) => defaultPageCoordinateFrame(page))
  };
}

function defaultPageCoordinateFrame(page) {
  const width = Number(page.width) || 0;
  const height = Number(page.height) || 0;
  return {
    pageNumber: page.number,
    width,
    height,
    bounds: { x: 0, y: 0, width, height },
    pageToNormalizedTransform: [safeInverse(width), 0, 0, safeInverse(height), 0, 0],
    normalizedToPageTransform: [width, 0, 0, height, 0, 0]
  };
}

function safeInverse(value) {
  return value ? 1 / value : 0;
}

function isPdfFile(file) {
  return file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf");
}

function isJsonFile(file) {
  return file.type === "application/json" || file.name.toLowerCase().endsWith(".json");
}

function isJsonLinesFile(file) {
  return file.name.toLowerCase().endsWith(".jsonl");
}

function isKvemoImageFile(file) {
  const name = file.name.toLowerCase();
  return file.type.startsWith("image/")
    || name.endsWith(".png")
    || name.endsWith(".jpg")
    || name.endsWith(".jpeg")
    || name.endsWith(".webp");
}

function formatNearbyText(values) {
  const items = Array.isArray(values) ? values : [];
  const text = items
    .map((item) => typeof item === "string" ? item : item?.text)
    .filter(Boolean);
  return text.length ? text.join(", ") : "-";
}

function setStatus(text) {
  elements.status.textContent = text;
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`;
}
