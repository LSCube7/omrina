import { OmrinaClient, checkHealth } from "@omrina/local-sdk";
import type { HealthStatus, TaskSnapshot, TemplateSummary } from "@omrina/local-sdk";

const client = new OmrinaClient({ endpoint: "http://127.0.0.1:17843" });
const health: Promise<HealthStatus> = checkHealth({ endpoint: "http://127.0.0.1:17843" });
const templates: Promise<TemplateSummary[]> = client.getTemplates();
const tasks: Promise<TaskSnapshot[]> = client.listTasks();

void [health, templates, tasks];
