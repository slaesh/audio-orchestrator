#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(__dirname, '..');

const contractsPath = path.join(
  rootDir,
  'apps/orchestrator/ClientApp/src/app/core/signalr/AudioOrchestrator.Contracts.ts'
);
const generatedDir = path.join(
  rootDir,
  'apps/orchestrator/ClientApp/src/app/generated'
);
const apiDir = path.join(generatedDir, 'api');
const modelsFilePath = path.join(generatedDir, 'model/models.ts');

if (!fs.existsSync(contractsPath)) {
  console.error(`Contracts file not found at: ${contractsPath}`);
  process.exit(1);
}

// 1. Extract all exported types from AudioOrchestrator.Contracts.ts
const contractsContent = fs.readFileSync(contractsPath, 'utf-8');
const contractTypes = new Set(
  [...contractsContent.matchAll(/export\s+type\s+([A-Za-z0-9_]+)/g)].map((m) => m[1])
);
const lowerContractTypes = new Set([...contractTypes].map((t) => t.toLowerCase()));

// 2. Fix models.ts: filter out local duplicate exports and re-export all contract types
if (fs.existsSync(modelsFilePath)) {
  const modelsContent = fs.readFileSync(modelsFilePath, 'utf-8');
  const lines = modelsContent.split('\n');

  const keptLines = lines.filter((line) => {
    const trimmed = line.trim();
    if (!trimmed) return false;
    if (trimmed.includes('@contracts')) return false;

    // Filter out exports pointing to files matching contract type names
    const match = trimmed.match(/^export\s+\*\s+from\s+['"]\.\/([^'"]+)['"];?$/);
    if (match) {
      const modelName = match[1];
      if (lowerContractTypes.has(modelName.toLowerCase())) {
        return false;
      }
    }
    return true;
  });

  const sortedContractTypes = [...contractTypes].sort();
  const contractsExport = `export type { ${sortedContractTypes.join(', ')} } from '@contracts';`;
  const updatedModelsContent = `${keptLines.join('\n')}\n${contractsExport}\n`;

  fs.writeFileSync(modelsFilePath, updatedModelsContent, 'utf-8');
  console.log(`Updated ${path.relative(rootDir, modelsFilePath)} with ${contractTypes.size} contract types.`);
}

// 3. Fix service files in apiDir: ensure all used contract types are imported
if (fs.existsSync(apiDir)) {
  const serviceFiles = fs.readdirSync(apiDir).filter((f) => f.endsWith('.service.ts'));

  for (const file of serviceFiles) {
    const filePath = path.join(apiDir, file);
    let content = fs.readFileSync(filePath, 'utf-8');

    // Strip existing @contracts import and optional preceding @ts-ignore
    // Also strip any '../model/...' imports for types that exist in @contracts
    const localModelContractImportRegex = new RegExp(
      `(?:\\/\\/\\s*@ts-ignore\\s*\\n)?import\\s+{\\s*(${[...contractTypes].join('|')})\\s*}\\s+from\\s+['"]\\.\\.\\/model\\/[^'"]+['"];?\\n?`,
      'g'
    );
    content = content.replace(localModelContractImportRegex, '');

    const cleanContent = content.replace(
      /(?:\/\/\s*@ts-ignore\s*\n)?import\s+{[^}]*}\s+from\s+['"]@contracts['"];?\n?/g,
      ''
    );

    // Find all contract types referenced in this service (excluding the import line itself)
    const usedTypes = [...contractTypes]
      .filter((t) => new RegExp(`\\b${t}\\b`).test(cleanContent))
      .sort();

    if (usedTypes.length === 0) {
      if (cleanContent !== content) {
        fs.writeFileSync(filePath, cleanContent, 'utf-8');
      }
      continue;
    }

    const newImport = `// @ts-ignore\nimport { ${usedTypes.join(', ')} } from '@contracts';\n`;
    const contractsImportRegex = /(?:\/\/\s*@ts-ignore\s*\n)?import\s+{[^}]*}\s+from\s+['"]@contracts['"];?\n?/;

    if (contractsImportRegex.test(content)) {
      content = content.replace(contractsImportRegex, newImport);
    } else {
      const target = '// @ts-ignore\nimport { BASE_PATH';
      if (content.includes(target)) {
        content = content.replace(target, `${newImport}\n${target}`);
      } else {
        content = `${newImport}\n${content}`;
      }
    }

    fs.writeFileSync(filePath, content, 'utf-8');
    console.log(`Updated imports in ${file}: [${usedTypes.join(', ')}]`);
  }
}
