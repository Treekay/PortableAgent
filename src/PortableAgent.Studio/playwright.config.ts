import { defineConfig } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
mkdirSync(resolve(root, 'TestResults'), { recursive: true });
// Dedicated database and fresh sample processes; never reuse a developer's running servers.
export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, retries: 0, timeout: 45000,
  use: { baseURL: 'http://localhost:5173', browserName: 'chromium', viewport: { width: 1440, height: 1000 }, screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  webServer: [
    { command: 'dotnet samples/PortableAgent.Sample.FlightBookingMcpServer/bin/Debug/net10.0/PortableAgent.Sample.FlightBookingMcpServer.dll', cwd: root, port: 5103, reuseExistingServer: false },
    { command: 'dotnet samples/PortableAgent.Sample.PetBoardingMcpServer/bin/Debug/net10.0/PortableAgent.Sample.PetBoardingMcpServer.dll', cwd: root, port: 5102, reuseExistingServer: false },
    { command: 'dotnet src/PortableAgent.Api/bin/Debug/net10.0/PortableAgent.Api.dll', cwd: root, url: 'http://localhost:5100/api/agents', reuseExistingServer: false,
      env: { DatabasePath: resolve(root, `TestResults/phase7a-${Date.now()}.db`) } },
    { command: 'npm run dev', url: 'http://localhost:5173', reuseExistingServer: false },
  ],
});
