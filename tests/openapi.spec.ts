import { test, expect } from '@playwright/test';
import SwaggerParser from '@apidevtools/swagger-parser';

test('generated API document is a valid OpenAPI specification with resolvable references', async ({ request }) => {
  const response = await request.get('http://127.0.0.1:5080/openapi/v1.json');
  expect(response.ok()).toBeTruthy();
  const document = await response.json();
  const validated = await SwaggerParser.validate(document);
  expect(validated.info.title).toBe('paprika API');
  for (const path of ['/api/operations', '/api/messaging', '/api/messaging/deadletters/{id}/replay', '/api/packets/{id}/deliveries', '/api/packets/{id}/reprocess', '/api/packets/{id}/cancel', '/api/workflows', '/api/workflows/catalog', '/api/workflows/default', '/api/workflows/catalog/{kind}/{id}/{version}/publish']) {
    expect(validated.paths).toHaveProperty(path);
  }
  for (const path of ['/api/quality/qa', '/api/training/models', '/api/references/datasets', '/api/extraction/definitions', '/api/recognition/templates', '/api/flows/library', '/api/operations-platform/tasks', '/api/operations-platform/retention']) {
    expect(validated.paths).toHaveProperty(path);
  }
});
