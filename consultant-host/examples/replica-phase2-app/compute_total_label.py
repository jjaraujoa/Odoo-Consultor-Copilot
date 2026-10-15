for record in self:
    amount = record.x_amount or 0.0
    record['x_total_label'] = str(amount)
