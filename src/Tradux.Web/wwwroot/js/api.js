// Cliente HTTP do microsserviço (o editor só fala HTTP com Tradux.Api —
// CONTAINERS.md): endpoints de desenho, sem estado, e os templates
// versionados pelo próprio serviço (/v1/templates, ADR-0025).

const baseUrl = () => (globalThis.TRADUX_EDITOR_CONFIG?.apiBaseUrl ?? '').replace(/\/$/, '');

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

// Credencial da API (Fase 9): só na sessão do navegador — nunca em
// localStorage nem na edição guardada; vai no cabeçalho Authorization.
const CREDENTIAL_KEY = 'tradux.apiKey';
let credential = null;
try {
  credential = sessionStorage.getItem(CREDENTIAL_KEY);
} catch {
  credential = null;
}

export function setCredential(secret) {
  credential = secret?.trim() || null;
  try {
    if (credential) sessionStorage.setItem(CREDENTIAL_KEY, credential);
    else sessionStorage.removeItem(CREDENTIAL_KEY);
  } catch {
    // armazenamento indisponível: a credencial vale só nesta página
  }
}

export const hasCredential = () => Boolean(credential);

/**
 * Chama a API e devolve o corpo. `answers`: status que não são sucesso HTTP mas
 * trazem um resultado a mostrar (ex.: 422 de publicação recusada pelos gates).
 */
async function request(method, path, body, { answers = [], forbidden = 'A credencial não tem permissão para esta ação.' } = {}) {
  const headers = body === undefined ? {} : { 'Content-Type': 'application/json' };
  if (credential) headers.Authorization = `ApiKey ${credential}`;
  let response;
  try {
    response = await fetch(`${baseUrl()}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(`Não foi possível falar com a API em ${baseUrl() || '(mesma origem)'}. Ela está em execução?`, 0);
  }

  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;
  if (response.ok || answers.includes(response.status)) return payload;
  if (response.status === 401) throw new ApiError('Credencial ausente ou inválida: informe a credencial da API no topo da página.', 401);
  if (response.status === 403) throw new ApiError(forbidden, 403);
  // ProblemDetails (400) traz a explicação em "detail".
  throw new ApiError(payload?.detail ?? payload?.title ?? `Erro HTTP ${response.status}.`, response.status);
}

export const listOperators = () => request('GET', '/v1/design/operators');

/** schema: { files: [{ name, contentBase64 }], entryPoint, rootElementName? } */
export const describeSchema = (schema) => request('POST', '/v1/design/schema', schema);

/** XSD de origem (Fase 12): mesma forma do schema; devolve a árvore de campos de origem. */
export const describeSourceSchema = (schema) => request('POST', '/v1/design/source-schema', schema);

/** XSD de destino atual × novo, com o mapeamento atual: mudanças e o que remapear (Fase 14, ADR-0017). */
export const compareSchemas = (body) => request('POST', '/v1/design/schema-changes', body);

/** draft: { schema, mapping, sourceSchema, sourceXml, lookupTables, expectedXml? } */
export const testDraft = (draft) => request('POST', '/v1/design/test', draft);

const template = (templateId) => `/v1/templates/${encodeURIComponent(templateId)}`;

/** Templates da organização da credencial, com a versão ativa. */
export const listTemplates = () => request('GET', '/v1/templates');

/** Histórico de versões (mais nova primeiro). */
export const listVersions = (templateId) => request('GET', `${template(templateId)}/versions`);

/** Conteúdo de uma versão: { schema, sourceSchema, mapping, lookupTables, scenarios, ... }. */
export const getVersion = (templateId, version) => request('GET', `${template(templateId)}/versions/${version}`);

/** Reativa uma versão anterior: 200 = { success, activeVersion }; 409 = recusado (devolve o motivo). */
export const rollback = (templateId, version) => request('POST', `${template(templateId)}/rollback`, { version },
  { answers: [409], forbidden: 'A credencial não tem permissão para reativar versões (template.publish).' });

/**
 * Publica a próxima versão do template (PUT /v1/templates/{nome}): 200 = publicada;
 * 422 = recusada pelos gates (devolve o resultado com diagnósticos e relatório, sem lançar).
 */
export const publishTemplate = (templateId, body) => request('PUT', template(templateId), body,
  { answers: [422], forbidden: 'A credencial não tem permissão para publicar (template.publish).' });

/** draft: { schema, mapping, lookupTables } — testes estruturais automáticos (Fase 8). */
export const checkQuality = (draft) => request('POST', '/v1/design/quality', draft);
