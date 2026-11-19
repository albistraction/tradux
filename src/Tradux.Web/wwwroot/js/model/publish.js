// Versão do template no serviço (Fase 22, ADR-0025): o pedido de
// PUT /v1/templates/{nome} e, na volta, o cenário de exemplo de uma versão
// aberta. A API roda os gates (compilação, testes estruturais, cenário
// funcional) e só então a versão vira a ativa. O cenário obrigatório sai do
// XML de exemplo + XML esperado. Módulo puro.

/** Nome de template aceito pela API (TemplateKey): letras, dígitos, ponto, hífen e sublinhado. */
export const TEMPLATE_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

const SCENARIO_NAME = 'XML de exemplo do editor';

/**
 * @param {{ templateId, schema, mapping, lookupTables, sourceSchema, sampleXml, expectedXml }} input
 * @returns {{ problems: string[], request: { templateId: string, body: object }|null }}
 */
export function buildPublication({ templateId, schema, mapping, lookupTables, sourceSchema, sampleXml, expectedXml }) {
  const problems = [];
  const name = templateId?.trim() ?? '';
  if (!name) problems.push('Informe o nome do template.');
  else if (!TEMPLATE_NAME.test(name)) problems.push('Nome do template: use letras, dígitos, ".", "-" ou "_" (ex.: dps-municipio).');
  if (!sourceSchema) problems.push('Carregue o XSD de origem — ele define o XML que o template aceita.');
  if (!schema || !mapping) problems.push('Carregue o XSD de destino e monte o mapeamento.');
  if (!sampleXml?.trim()) problems.push('Informe um XML de exemplo (formato do XSD de origem) — ele vira o cenário funcional obrigatório.');
  if (!expectedXml?.trim()) problems.push('Informe o XML esperado para o XML de exemplo — a publicação exige um cenário com resultado esperado.');
  if (problems.length > 0) return { problems, request: null };

  return {
    problems,
    request: {
      templateId: name,
      body: {
        schema,
        sourceSchema,
        mapping,
        lookupTables: lookupTables ?? {},
        scenarios: [{ name: SCENARIO_NAME, sourceXml: sampleXml, expectedXml }],
      },
    },
  };
}

export function describePublication(result) {
  if (!result) return null;
  if (result.success) return { ok: true, text: `Publicado: versão ${result.snapshotVersion} ativa.` };
  const codes = [...new Set((result.diagnostics ?? []).map((d) => d.code))];
  return { ok: false, text: `Publicação recusada pelos gates (${codes.join(', ') || 'sem código'}). A versão ativa não mudou.` };
}

/** Cenário de exemplo de uma versão: o primeiro com XML de origem e XML esperado (senão, o primeiro com XML de origem). */
export function sampleScenario(scenarios) {
  const withXml = (scenarios ?? []).find((s) => s.sourceXml && s.expectedXml) ?? (scenarios ?? []).find((s) => s.sourceXml);
  return { sampleXml: withXml?.sourceXml ?? '', expectedXml: withXml?.expectedXml ?? '' };
}
