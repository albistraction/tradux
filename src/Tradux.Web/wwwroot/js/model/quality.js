// Relatório dos testes estruturais automáticos (Fase 8, seção 19) — o que
// POST /v1/design/quality devolve. Módulo puro: agrupa e rotula, sem DOM.

export const CATEGORY_LABEL = {
  Required: 'Só obrigatórios',
  Complete: 'Obrigatórios e opcionais',
  LowerBound: 'Limites mínimos',
  UpperBound: 'Limites máximos',
  Enumeration: 'Enumerações',
  Repetition: 'Repetições',
  Null: 'Nulos permitidos',
  Choice: 'Ramos de choice',
  Invalid: 'Inválidos esperados',
};

const ORDER = Object.keys(CATEGORY_LABEL);

/**
 * Resume o relatório por categoria, na ordem da seção 19.
 * @returns {{ category: string, label: string, total: number, passed: number, failed: object[] }[]}
 */
export function summarizeQuality(report) {
  const groups = new Map();
  for (const testCase of report?.cases ?? []) {
    const group = groups.get(testCase.category) ?? { category: testCase.category, label: CATEGORY_LABEL[testCase.category] ?? testCase.category, total: 0, passed: 0, failed: [] };
    group.total += 1;
    if (testCase.passed) group.passed += 1;
    else group.failed.push(testCase);
    groups.set(testCase.category, group);
  }

  return [...groups.values()].sort((a, b) => rank(a.category) - rank(b.category));
}

function rank(category) {
  const index = ORDER.indexOf(category);
  return index < 0 ? ORDER.length : index;
}

/** Texto curto de um caso reprovado (sem dados — o relatório não os tem). */
export function describeFailure(testCase) {
  return testCase.expectedValid
    ? `${testCase.name}: entrada válida não gerou XML válido (${testCase.failureCode ?? 'erro'})`
    : `${testCase.name}: entrada inválida foi aceita${testCase.target ? ` (${testCase.target})` : ''}`;
}
