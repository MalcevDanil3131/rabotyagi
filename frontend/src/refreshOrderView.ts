export async function refreshOrderView(input: {
  loadList: () => Promise<unknown>;
  loadWorkers: () => Promise<unknown>;
  loadDetail: () => Promise<unknown>;
  selected: string | null;
  reportId: string | null;
  afterMutation: boolean;
}) {
  await Promise.all([
    input.loadList(), input.loadWorkers(),
    input.selected && (!input.reportId || input.afterMutation) ? input.loadDetail() : Promise.resolve(),
  ]);
}
